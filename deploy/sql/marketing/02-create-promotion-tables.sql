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

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;