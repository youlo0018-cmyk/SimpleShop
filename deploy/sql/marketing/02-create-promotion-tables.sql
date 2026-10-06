-- 营销活动表（满减 / 满折 / 满赠 / 限时抢购），幂等可重跑
-- 依据：BUSINESS.md 11.1~11.3 活动规则 / 12 限时抢购
--
-- 活动与券是两个**并列**的优惠来源：活动不需要领取、直接作用于下单行；
-- 券要先领、订单级只能有一张。11.2 的「每个订单行只能命中 1 个活动或 1 张券」就是它们的互斥关系。

CREATE TABLE IF NOT EXISTS promotion_activity (
    id               bigint        NOT NULL,
    created_at       timestamp     NOT NULL,
    updated_at       timestamp     NULL,
    is_deleted       boolean       NOT NULL DEFAULT false,
    deleted_at       timestamp     NULL,
    -- 创建人写入后永不修改；操作人每次更新被覆盖（DATA_SPEC 2.2）
    created_by_id    bigint        NOT NULL DEFAULT 0,
    created_by_name  varchar(64)   NOT NULL DEFAULT '',
    operation_id     bigint        NOT NULL DEFAULT 0,
    operation_name   varchar(64)   NOT NULL DEFAULT '',
    platform_id      bigint        NOT NULL DEFAULT 0,
    merchant_id      bigint        NOT NULL DEFAULT 0,
    activity_name    varchar(128)  NOT NULL DEFAULT '',
    -- 1 满减 / 2 满折 / 3 满赠 / 4 限时抢购
    activity_type    int           NOT NULL DEFAULT 1,
    -- 门槛金额。判定基数是「适用行金额合计」，不是订单总额
    threshold_amount numeric(18,2) NOT NULL DEFAULT 0,
    discount_amount  numeric(18,2) NOT NULL DEFAULT 0,
    -- 折扣率数值 0.01~10，8.5 表示 85 折
    discount_rate    numeric(18,2) NOT NULL DEFAULT 0,
    -- 满赠赠送的券模板 Id
    gift_template_id bigint        NOT NULL DEFAULT 0,
    -- 满赠每单赠送张数，1 ~ 100。非满赠活动忽略
    gift_quantity    int           NOT NULL DEFAULT 1,
    -- 限时抢购场次 Id。非秒杀固定 0。
    -- 所有查询都按它寻址，「当前场次」只是查询条件之一——多场次能力现在就具备，
    -- 不用等做秒杀时再改表。
    session_id       bigint        NOT NULL DEFAULT 0,
    -- 适用范围：1 全场 / 2 指定 SPU / 3 指定 SKU
    target_type      int           NOT NULL DEFAULT 1,
    -- 适用范围的 JSON 文本，结构随 target_type 变化
    targets          varchar(4096) NOT NULL DEFAULT '[]',
    -- 时间窗一律 UTC。「使用时按 now 过滤」不缓存，所以不需要把窗口拍到券上
    start_time       timestamp     NOT NULL,
    end_time         timestamp     NOT NULL,
    per_order_limit  int           NOT NULL DEFAULT 0,
    total_quantity   int           NOT NULL DEFAULT 0,
    used_quantity    int           NOT NULL DEFAULT 0,
    sort_order       int           NOT NULL DEFAULT 0,
    status           int           NOT NULL DEFAULT 1,
    CONSTRAINT pk_promotion_activity PRIMARY KEY (id)
);

-- 结算试算的热点路径：按「时间窗 + 状态 + 平台」筛出候选活动。
-- 不建这个索引的话，订单量上来后每次试算都要全表扫，而试算在列表页是每屏都要跑一次。
CREATE INDEX IF NOT EXISTS idx_promotion_active
    ON promotion_activity (status, start_time, end_time)
    WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS idx_promotion_platform
    ON promotion_activity (platform_id, merchant_id)
    WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS idx_promotion_session
    ON promotion_activity (session_id)
    WHERE is_deleted = false AND session_id <> 0;

-- 审计列补齐：CREATE TABLE IF NOT EXISTS 不会给已存在的表补列，
-- 所以对本文件之前已经建过表的库，这四条 ALTER 必须留着（幂等，可重复执行）。
ALTER TABLE promotion_activity ADD COLUMN IF NOT EXISTS created_by_id   bigint      NOT NULL DEFAULT 0;
ALTER TABLE promotion_activity ADD COLUMN IF NOT EXISTS created_by_name varchar(64) NOT NULL DEFAULT '';
ALTER TABLE promotion_activity ADD COLUMN IF NOT EXISTS operation_id    bigint      NOT NULL DEFAULT 0;
ALTER TABLE promotion_activity ADD COLUMN IF NOT EXISTS operation_name  varchar(64) NOT NULL DEFAULT '';
ALTER TABLE promotion_activity ADD COLUMN IF NOT EXISTS gift_quantity   int         NOT NULL DEFAULT 1;

-- 满赠待发券记录（BUSINESS.md 11.6 / 20.1：满赠发券）。
--
-- 「命中满赠」发生在上单试算那一刻（按当时的活动时间窗与配置判定），
-- 「发券」发生在支付成功。中间隔着用户付款这段时间，支付时再重算一遍的话，
-- 活动一旦被改或过期，用户就会「下单页写着送券、付完钱没有」。
-- 所以下单试算时先把发放承诺落成一条记录，支付成功只按记录发券。
CREATE TABLE IF NOT EXISTS gift_grant (
    id               bigint        NOT NULL,
    created_at       timestamp     NOT NULL,
    updated_at       timestamp     NULL,
    is_deleted       boolean       NOT NULL DEFAULT false,
    deleted_at       timestamp     NULL,
    order_no         varchar(64)   NOT NULL DEFAULT '',
    customer_id      bigint        NOT NULL DEFAULT 0,
    -- 1 满赠活动 / 2 满赠券
    source_type      int           NOT NULL DEFAULT 1,
    -- 来源 Id：活动 Id 或用户券 Id
    source_id        bigint        NOT NULL DEFAULT 0,
    gift_template_id bigint        NOT NULL DEFAULT 0,
    quantity         int           NOT NULL DEFAULT 1,
    -- 10 待发放 / 20 已发放
    status           int           NOT NULL DEFAULT 10,
    issued_at        timestamp     NULL,
    CONSTRAINT pk_gift_grant PRIMARY KEY (id)
);

-- 幂等键：同一单、同一来源只承诺一次。
-- 下单试算会被重试（客户端重试、幂等键撞车后的重放），
-- 没有这个索引就会承诺两次，付完钱发两份券。
CREATE UNIQUE INDEX IF NOT EXISTS uk_gift_grant_order_source
    ON gift_grant (order_no, source_type, source_id);

-- 发放与补偿重试都按「订单号 + 状态」找记录
CREATE INDEX IF NOT EXISTS idx_gift_grant_order ON gift_grant (order_no, status);

-- 对账 / 排障按状态筛「还没发出去的承诺」
CREATE INDEX IF NOT EXISTS idx_gift_grant_status
    ON gift_grant (status)
    WHERE is_deleted = false;

-- 活动参与记录（BUSINESS.md 17「活动：参与订单数、参与金额、折扣总额」+ 下钻订单明细）。
--
-- 订单行只存「这行减了多少钱」，**不存命中了哪个活动**，所以活动报表没法从订单侧反推。
-- 判定活动命中的地方只有一处：下单试算。所以试算时就把「这单命中了哪个活动、减了多少」
-- 记下来，报表按它聚合，下钻就是按 activity_id 翻这张表。
--
-- 活动名存快照：活动可以改名甚至软删，报表要显示**当时**的名字，
-- 联表取当前值会让历史报表跟着改名。
CREATE TABLE IF NOT EXISTS marketing_activity_record (
    id              bigint        NOT NULL,
    created_at      timestamp     NOT NULL,
    updated_at      timestamp     NULL,
    is_deleted      boolean       NOT NULL DEFAULT false,
    deleted_at      timestamp     NULL,
    order_no        varchar(64)   NOT NULL DEFAULT '',
    customer_id     bigint        NOT NULL DEFAULT 0,
    activity_id     bigint        NOT NULL DEFAULT 0,
    activity_name   varchar(128)  NOT NULL DEFAULT '',
    platform_id     bigint        NOT NULL DEFAULT 0,
    merchant_id     bigint        NOT NULL DEFAULT 0,
    discount_amount numeric(18,2) NOT NULL DEFAULT 0,
    CONSTRAINT pk_marketing_activity_record PRIMARY KEY (id)
);

-- 幂等键：同一单同一活动只记一次。试算会被重放（客户端重试、幂等键撞车），
-- 没有这个索引就会把「参与订单数」刷成两倍。
CREATE UNIQUE INDEX IF NOT EXISTS uk_activity_record_order_activity
    ON marketing_activity_record (order_no, activity_id);

-- 报表按活动 + 时间聚合；下钻按活动翻页
CREATE INDEX IF NOT EXISTS idx_activity_record_activity
    ON marketing_activity_record (activity_id, created_at)
    WHERE is_deleted = false;

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;
