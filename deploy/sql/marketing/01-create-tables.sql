-- MarketingService 建表脚本（幂等，可重复执行）
-- 依据：DATA_SPEC.md 5.12 券模板 / 5.13 券活动 / 5.14 营销配置，BUSINESS.md 11.3~11.7 券结算与占券
--
-- 本轮落地的是「券」这条线（订单链路的 ① 占券要用）：
--   券模板 → 券活动发券 → 用户券（带模板快照）→ 占券 / 核销 / 回退
-- 满减 / 满折 / 满赠「活动」与限时抢购尚未落地，接口先留着。

-- 券模板。改模板**不影响已发出的券**（已发出的券按自己的快照算）。
CREATE TABLE IF NOT EXISTS coupon_template (
    id            bigint        NOT NULL,
    created_at    timestamp     NOT NULL,
    updated_at    timestamp     NULL,
    is_deleted    boolean       NOT NULL DEFAULT false,
    deleted_at    timestamp     NULL,
    created_by_id bigint        NOT NULL DEFAULT 0,
    created_by_name varchar(64) NOT NULL DEFAULT '',
    operation_id  bigint        NOT NULL DEFAULT 0,
    operation_name varchar(64)  NOT NULL DEFAULT '',
    platform_id   bigint        NOT NULL DEFAULT 0,
    merchant_id   bigint        NOT NULL DEFAULT 0,
    template_name varchar(128)  NOT NULL,
    coupon_type   int           NOT NULL,
    threshold_amount numeric(18,2) NOT NULL DEFAULT 0,
    discount_amount numeric(18,2) NOT NULL DEFAULT 0,
    discount_rate numeric(5,2)  NOT NULL DEFAULT 0,
    gift_template_id bigint      NOT NULL DEFAULT 0,
    valid_days    int           NOT NULL DEFAULT 30,
    total_quantity int          NOT NULL DEFAULT 0,
    issued_quantity int         NOT NULL DEFAULT 0,
    per_user_limit int          NOT NULL DEFAULT 1,
    per_order_limit int         NOT NULL DEFAULT 1,
    sort_order    int           NOT NULL DEFAULT 0,
    status        int           NOT NULL DEFAULT 1,
    CONSTRAINT pk_coupon_template PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_coupon_template_status ON coupon_template (platform_id, status);

-- 券活动（领券中心）。发放量与模板 TotalQuantity 是**两个独立池子**。
CREATE TABLE IF NOT EXISTS coupon_activity (
    id            bigint        NOT NULL,
    created_at    timestamp     NOT NULL,
    updated_at    timestamp     NULL,
    is_deleted    boolean       NOT NULL DEFAULT false,
    deleted_at    timestamp     NULL,
    created_by_id bigint        NOT NULL DEFAULT 0,
    created_by_name varchar(64) NOT NULL DEFAULT '',
    operation_id  bigint        NOT NULL DEFAULT 0,
    operation_name varchar(64)  NOT NULL DEFAULT '',
    platform_id   bigint        NOT NULL DEFAULT 0,
    merchant_id   bigint        NOT NULL DEFAULT 0,
    activity_name varchar(128)  NOT NULL,
    template_id   bigint        NOT NULL,
    claim_start_time timestamp  NOT NULL,
    claim_end_time   timestamp  NOT NULL,
    claim_quantity int          NOT NULL DEFAULT 1,
    claimed_quantity int        NOT NULL DEFAULT 0,
    per_user_limit int          NOT NULL DEFAULT 1,
    target_type   int           NOT NULL DEFAULT 1,
    targets       varchar(2000) NOT NULL DEFAULT '[]',
    sort_order    int           NOT NULL DEFAULT 0,
    status        int           NOT NULL DEFAULT 1,
    CONSTRAINT pk_coupon_activity PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_coupon_activity_status ON coupon_activity (platform_id, status, claim_start_time);

-- 用户券（券包）。
-- 🔴 模板的类型 / 门槛 / 优惠额 / 折扣率 / 有效期天数在发放时**复制一份快照**存这里，
-- 之后模板怎么改都不影响这张券 —— 这是 DATA_SPEC 5.12 明确要求的。
CREATE TABLE IF NOT EXISTS user_coupon (
    id            bigint        NOT NULL,
    created_at    timestamp     NOT NULL,
    updated_at    timestamp     NULL,
    is_deleted    boolean       NOT NULL DEFAULT false,
    deleted_at    timestamp     NULL,
    customer_id   bigint        NOT NULL,
    template_id   bigint        NOT NULL,
    activity_id   bigint        NOT NULL DEFAULT 0,
    coupon_code   varchar(32)   NOT NULL,
    -- ↓↓↓ 以下 5 个是快照字段，不与模板联动 ↓↓↓
    coupon_type   int           NOT NULL,
    threshold_amount numeric(18,2) NOT NULL DEFAULT 0,
    discount_amount numeric(18,2) NOT NULL DEFAULT 0,
    discount_rate numeric(5,2)  NOT NULL DEFAULT 0,
    valid_days    int           NOT NULL DEFAULT 30,
    -- ↑↑↑ 快照结束 ↑↑↑
    target_type   int           NOT NULL DEFAULT 1,
    targets       varchar(2000) NOT NULL DEFAULT '[]',
    -- 1 未使用 / 2 已占用（下单锁定）/ 3 已核销 / 4 已过期
    status        int           NOT NULL DEFAULT 1,
    expire_at     timestamp     NOT NULL,
    receive_at    timestamp     NOT NULL,
    order_no      varchar(64)   NOT NULL DEFAULT '',
    consume_at    timestamp     NULL,
    CONSTRAINT pk_user_coupon PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uk_user_coupon_code ON user_coupon (coupon_code);
-- 查券包与最优券都按「客户 + 可用状态 + 到期时间」走
CREATE INDEX IF NOT EXISTS idx_user_coupon_customer ON user_coupon (customer_id, status, expire_at);
CREATE UNIQUE INDEX IF NOT EXISTS uk_user_coupon_occupancy
    ON user_coupon (customer_id, order_no) WHERE status = 2 AND order_no <> '';

-- 占券记录。一笔订单只能占一张券（BUSINESS.md 11.3「订单级唯一一张」）。
CREATE TABLE IF NOT EXISTS coupon_occupancy (
    id            bigint      NOT NULL,
    created_at    timestamp   NOT NULL,
    updated_at    timestamp   NULL,
    is_deleted    boolean     NOT NULL DEFAULT false,
    deleted_at    timestamp   NULL,
    customer_id   bigint      NOT NULL,
    order_no      varchar(64) NOT NULL,
    coupon_id     bigint      NOT NULL,
    discount_amount numeric(18,2) NOT NULL DEFAULT 0,
    -- 1 已占券 / 2 已核销 / 3 已回退
    status        int         NOT NULL DEFAULT 1,
    CONSTRAINT pk_coupon_occupancy PRIMARY KEY (id)
);

-- 同一订单最多一条「已占券」记录，重复下单会被唯一索引挡住
CREATE UNIQUE INDEX IF NOT EXISTS uk_coupon_occupancy_order
    ON coupon_occupancy (order_no) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS idx_coupon_occupancy_coupon ON coupon_occupancy (coupon_id);

-- 营销配置：平台优惠优先级。每平台一条。
CREATE TABLE IF NOT EXISTS marketing_config (
    id            bigint      NOT NULL,
    created_at    timestamp   NOT NULL,
    updated_at    timestamp   NULL,
    is_deleted    boolean     NOT NULL DEFAULT false,
    deleted_at    timestamp   NULL,
    platform_id   bigint      NOT NULL,
    -- 1 活动优先 / 2 券优先（默认券优先）
    priority      int         NOT NULL DEFAULT 2,
    CONSTRAINT pk_marketing_config PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uk_marketing_config_platform
    ON marketing_config (platform_id) WHERE is_deleted = false;

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;