-- 限时抢购：场次 + 场次商品（幂等可重跑）
-- 依据：BUSINESS.md 12.2 / 12.3 / 12.4
--
-- **本期只做单场次，但所有实体与接口都按 SessionId 寻址**，「当前场次」只是查询条件之一。
-- 现在不建「场次列表」以外的抽象，以后加多场次也不用改表。

-- ---------------------------------------------------------------- 场次
CREATE TABLE IF NOT EXISTS seckill_session (
    id                  bigint        NOT NULL,
    created_at          timestamp     NOT NULL,
    updated_at          timestamp     NULL,
    is_deleted          boolean       NOT NULL DEFAULT false,
    deleted_at          timestamp     NULL,
    created_by_id       bigint        NOT NULL DEFAULT 0,
    created_by_name     varchar(64)   NOT NULL DEFAULT '',
    operation_id        bigint        NOT NULL DEFAULT 0,
    operation_name      varchar(64)   NOT NULL DEFAULT '',
    platform_id         bigint        NOT NULL DEFAULT 0,
    merchant_id         bigint        NOT NULL DEFAULT 0,
    session_name        varchar(128)  NOT NULL DEFAULT '',
    start_time          timestamp     NOT NULL,
    end_time            timestamp     NOT NULL,
    -- 10 未开始 / 20 进行中 / 30 已结束 / 40 已取消（手动中止）
    status              int           NOT NULL DEFAULT 10,
    -- 常规库存是否**已经划出**到本场次。
    -- 这个标志是整个库存方案的关键：重复点「发布」不能把库存再划一遍。
    stock_transferred   boolean       NOT NULL DEFAULT false,
    sort_order          int           NOT NULL DEFAULT 0,
    CONSTRAINT pk_seckill_session PRIMARY KEY (id),
    -- 同一平台同一时间不允许两个重叠场次：重叠会让同一批库存被两个场次各划一次
    CONSTRAINT ck_seckill_session_time CHECK (end_time > start_time)
);

CREATE INDEX IF NOT EXISTS idx_seckill_session_status
    ON seckill_session (status, start_time)
    WHERE is_deleted = false;

-- ---------------------------------------------------------------- 场次商品
CREATE TABLE IF NOT EXISTS seckill_item (
    id                bigint        NOT NULL,
    created_at        timestamp     NOT NULL,
    updated_at        timestamp     NULL,
    is_deleted        boolean       NOT NULL DEFAULT false,
    deleted_at        timestamp     NULL,
    created_by_id     bigint        NOT NULL DEFAULT 0,
    created_by_name   varchar(64)   NOT NULL DEFAULT '',
    operation_id      bigint        NOT NULL DEFAULT 0,
    operation_name    varchar(64)   NOT NULL DEFAULT '',
    platform_id       bigint        NOT NULL DEFAULT 0,
    merchant_id       bigint        NOT NULL DEFAULT 0,
    -- 场次 Id。**所有查询都按它寻址**，这是多场次能力的落点
    session_id        bigint        NOT NULL,
    spu_id            bigint        NOT NULL,
    sku_id            bigint        NOT NULL,
    product_name      varchar(128)  NOT NULL DEFAULT '',
    sku_spec_text     varchar(256)  NOT NULL DEFAULT '',
    image             varchar(512)  NOT NULL DEFAULT '',
    -- 秒杀价。**必须低于该 SKU 的常规售价**，否则「秒杀」比原价还贵
    seckill_price     numeric(18,2) NOT NULL,
    -- 划线原价快照。加商品时从商品服务取当时的售价存下来。
    -- 为什么不实时查：场次是提前几天建好的，期间商品可能调价、甚至下架；
    -- 前台展示「原价划线 + 秒杀价大字」需要的是**建场次那一刻**的价，
    -- 实时查会把后来调过的价当成原价，出现「划线价比现在还低」这种笑话。
    original_price    numeric(18,2) NOT NULL DEFAULT 0,
    -- 划出到本场次的库存量（从常规库存 available 减掉的那部分）
    seckill_stock     int           NOT NULL DEFAULT 0,
    -- 每人每场次限购，1 起
    per_user_limit    int           NOT NULL DEFAULT 1,
    -- 已抢数量。<seckill_stock 才是还有货
    sold_count        int           NOT NULL DEFAULT 0,
    status            int           NOT NULL DEFAULT 1,
    sort_order        int           NOT NULL DEFAULT 0,
    CONSTRAINT pk_seckill_item PRIMARY KEY (id),
    -- 同一场次内同一个 SKU 只能有一条，否则限购与库存都会被算两遍
    CONSTRAINT uk_seckill_item_session_sku UNIQUE (session_id, sku_id),
    CONSTRAINT ck_seckill_item_stock CHECK (seckill_stock >= 0),
    CONSTRAINT ck_seckill_item_sold CHECK (sold_count >= 0),
    -- 核心防超卖约束：**已抢数量不得超过划出的库存**。
    -- Redis 预扣是第一道防线，这条数据库约束是最后一道——
    -- Redis 挂了、缓存丢了、代码有 bug 时，它保证 sold 永远不会超过库存。
    CONSTRAINT ck_seckill_item_not_oversold CHECK (sold_count <= seckill_stock)
);

CREATE INDEX IF NOT EXISTS idx_seckill_item_session
    ON seckill_item (session_id, status)
    WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS idx_seckill_item_sku
    ON seckill_item (sku_id)
    WHERE is_deleted = false;

-- ---------------------------------------------------------------- 抢购请求
-- 一行一次抢购尝试。request_id 是客户端轮询结果的凭据，
-- 也是幂等键 {seckillItemId}:{customerId} 的载体。
CREATE TABLE IF NOT EXISTS seckill_grab (
    id                bigint        NOT NULL,
    created_at        timestamp     NOT NULL,
    updated_at        timestamp     NULL,
    is_deleted        boolean       NOT NULL DEFAULT false,
    deleted_at        timestamp     NULL,
    platform_id       bigint        NOT NULL DEFAULT 0,
    merchant_id       bigint        NOT NULL DEFAULT 0,
    request_id        varchar(64)   NOT NULL,
    -- 幂等键：{seckillItemId}:{customerId}。同一人同一场次商品只能有一条
    biz_no            varchar(128)  NOT NULL,
    session_id        bigint        NOT NULL,
    item_id           bigint        NOT NULL,
    customer_id       bigint        NOT NULL,
    quantity          int           NOT NULL DEFAULT 1,
    -- 0 处理中 / 1 成功 / 2 已被抢完 / 3 不在抢购中 / 4 超限购 / 5 下单失败
    result_status     int           NOT NULL DEFAULT 0,
    result_message    varchar(128)  NOT NULL DEFAULT '',
    order_id          bigint        NOT NULL DEFAULT 0,
    order_no          varchar(64)   NOT NULL DEFAULT '',
    CONSTRAINT pk_seckill_grab PRIMARY KEY (id)
);

-- 幂等的最后防线。Redis 预扣 + 应用层判断已经挡掉了绝大多数重复，
-- 这一条保证两个并发请求抢同一份限购额度时只有一个能落库。
CREATE UNIQUE INDEX IF NOT EXISTS uk_seckill_grab_biz
    ON seckill_grab (biz_no) WHERE is_deleted = false;

CREATE UNIQUE INDEX IF NOT EXISTS uk_seckill_grab_request
    ON seckill_grab (request_id) WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS idx_seckill_grab_customer
    ON seckill_grab (customer_id, created_at)
    WHERE is_deleted = false;

-- 建表已存在的库（如本文件首次执行前就建过）补列，保证幂等可重跑
ALTER TABLE seckill_session ADD COLUMN IF NOT EXISTS stock_transferred boolean NOT NULL DEFAULT false;
ALTER TABLE seckill_item   ADD COLUMN IF NOT EXISTS original_price    numeric(18,2) NOT NULL DEFAULT 0;

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;