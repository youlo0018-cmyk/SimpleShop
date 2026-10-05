-- PointService 建表脚本（幂等，可重复执行）
-- 依据：BUSINESS.md 13（积分：模型 / 获取 / 消耗 / 冻结 / 过期 / 签到 / 上限幂等 / 流水）
--
-- 与库存同构的两个计数：available（可抵扣）/ frozen（下单冻结中）。
-- 冻结模型是必须的：下单时先把积分划走，支付成功才真正扣掉，取消 / 超时要能原路退回。

-- 积分账户。**全局唯一**（跨平台共用一个余额，BUSINESS.md 13.1），
-- 所以主键是 customer_id 而不是 (customer_id, platform_id)。
CREATE TABLE IF NOT EXISTS point_account (
    id            bigint        NOT NULL,
    created_at    timestamp     NOT NULL,
    updated_at    timestamp     NULL,
    is_deleted    boolean       NOT NULL DEFAULT false,
    deleted_at    timestamp     NULL,
    customer_id   bigint        NOT NULL,
    available     bigint        NOT NULL DEFAULT 0,
    frozen        bigint        NOT NULL DEFAULT 0,
    total_earned  bigint        NOT NULL DEFAULT 0,
    total_used    bigint        NOT NULL DEFAULT 0,
    -- 签到连续天数，断签清零；7 天一轮
    sign_streak     int          NOT NULL DEFAULT 0,
    sign_last_date  date         NULL,
    CONSTRAINT pk_point_account PRIMARY KEY (id)
);

-- 🔴 一个客户一个账户。用 partial unique 让软删的那条不占坑。
CREATE UNIQUE INDEX IF NOT EXISTS uk_point_account_customer
    ON point_account (customer_id) WHERE is_deleted = false;

-- 积分批次（发放时创建，365 天到期）。
-- 「FIFO 先到期先用」和「退款回到**原批次**、不重新计算有效期」这两条规则，
-- 都要求积分必须记得到**是哪一批**，只记一个总余额是做不到的。
CREATE TABLE IF NOT EXISTS point_lot (
    id           bigint      NOT NULL,
    created_at   timestamp   NOT NULL,
    updated_at   timestamp   NULL,
    is_deleted   boolean     NOT NULL DEFAULT false,
    deleted_at   timestamp   NULL,
    customer_id  bigint      NOT NULL,
    source       varchar(32) NOT NULL,
    biz_no       varchar(64) NOT NULL DEFAULT '',
    total        bigint      NOT NULL DEFAULT 0,
    remaining    bigint      NOT NULL DEFAULT 0,
    expire_at    timestamp   NOT NULL,
    CONSTRAINT pk_point_lot PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_point_lot_customer ON point_lot (customer_id, expire_at);
CREATE INDEX IF NOT EXISTS idx_point_lot_expire ON point_lot (expire_at) WHERE remaining > 0;

-- 冻结批次。下单锁定时把「从哪些发放批次里划走的、各划走多少」记下来，
-- 这样取消 / 超时能原路退回、退款能按比例回到原批次 —— 都不需要重新推算有效期。
CREATE TABLE IF NOT EXISTS point_lock (
    id           bigint      NOT NULL,
    created_at   timestamp   NOT NULL,
    updated_at   timestamp   NULL,
    is_deleted   boolean     NOT NULL DEFAULT false,
    deleted_at   timestamp   NULL,
    customer_id  bigint      NOT NULL,
    biz_no       varchar(64) NOT NULL,
    quantity     bigint      NOT NULL,
    -- 0 冻结中 / 1 已实扣 / 2 已解冻 / 3 已按比例回收
    status       int         NOT NULL DEFAULT 0,
    CONSTRAINT pk_point_lock PRIMARY KEY (id)
);

-- 同一个业务号只能冻一次 —— 幂等的另一层保证（BUSINESS.md 13.7）
CREATE UNIQUE INDEX IF NOT EXISTS uk_point_lock_biz
    ON point_lock (customer_id, biz_no);

CREATE TABLE IF NOT EXISTS point_lock_lot (
    id        bigint NOT NULL,
    lock_id   bigint NOT NULL,
    lot_id    bigint NOT NULL,
    quantity  bigint NOT NULL,
    created_at timestamp   NOT NULL,
    CONSTRAINT pk_point_lock_lot PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_point_lock_lot_lock ON point_lock_lot (lock_id);

-- 积分流水。变动前后的余额冗余在这里，列表直接显示不必回表算。
CREATE TABLE IF NOT EXISTS point_record (
    id               bigint       NOT NULL,
    created_at       timestamp    NOT NULL,
    updated_at       timestamp    NULL,
    -- 流水原则上只追加不改；保留软删列是为了和 EntityBase / GlobalFilter 对齐，
    -- 不然这张表会被全局过滤直接查不出来。
    is_deleted       boolean      NOT NULL DEFAULT false,
    deleted_at       timestamp    NULL,
    customer_id      bigint       NOT NULL,
    biz_no           varchar(64)  NOT NULL,
    action           varchar(16)  NOT NULL,
    quantity         bigint       NOT NULL,
    before_available bigint       NOT NULL DEFAULT 0,
    after_available  bigint       NOT NULL DEFAULT 0,
    before_frozen    bigint       NOT NULL DEFAULT 0,
    after_frozen     bigint       NOT NULL DEFAULT 0,
    lot_expire_at    timestamp    NULL,
    remark           varchar(512) NOT NULL DEFAULT '',
    CONSTRAINT pk_point_record PRIMARY KEY (id)
);

-- 🔴 幂等键：BizNo + Action + CustomerId（BUSINESS.md 13.7）。
-- 用数据库唯一索引而不是「先查再插」——并发下后者必然漏，重复发放积分比少发严重得多。
CREATE UNIQUE INDEX IF NOT EXISTS uk_point_record_idem
    ON point_record (customer_id, biz_no, action) WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS idx_point_record_customer ON point_record (customer_id, created_at);

-- ============================================================================
-- 积分规则配置（权限点 point:rule-update，后台「积分规则维护」页）
--
-- 设计取舍：为什么是「覆盖表」而不是「把规则做成行」——
--   规则只有 7 条且**永远存在**（代码里 PointRules 有默认值），
--   做成行的话每次发放积分都要查一次配置表来拿规则，
--   而积分是下单主链路上的同步调用，多一次查询就多一个抖动点。
--   所以这里是「只有被改过的规则才进表」，查不到就用代码默认值。
--   这也让单元测试在没有这张表数据时仍然跑出与规格一致的结果。
--
-- rule_key 用规则名（如 balance_cap / sign_in_rewards），
-- rule_value 用文本存，签到奖励存 JSON 数组 [1,2,3,5,8,10,15]。
-- 数值列存字符串是刻意的：不同规则的取值形态不同（整数 / 小数 / JSON），
-- 强行拆成多列会为了「列好看」把规则模型搞复杂。
-- ============================================================================
CREATE TABLE IF NOT EXISTS point_rule_config (
    id            bigint        NOT NULL,
    created_at    timestamp     NOT NULL,
    updated_at    timestamp     NULL,
    is_deleted    boolean       NOT NULL DEFAULT false,
    deleted_at    timestamp     NULL,
    rule_key      varchar(64)   NOT NULL,
    rule_value    varchar(512)  NOT NULL,
    description   varchar(256)  NOT NULL DEFAULT '',
    updated_by_id bigint        NOT NULL DEFAULT 0,
    updated_by_name varchar(64) NOT NULL DEFAULT '',
    CONSTRAINT pk_point_rule_config PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uk_point_rule_config_key
    ON point_rule_config (rule_key) WHERE is_deleted = false;

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;
