-- OrderService 建表脚本（幂等，可重复执行）
-- 依据：BUSINESS.md 7 订单状态机 / 8.1 下单链路 / 8.4 金额口径

-- 订单主表。金额字段全部 numeric(18,2)，但**入库前已在应用层用 AwayFromZero 舍好**：
-- PostgreSQL 的 numeric→numeric(18,2) 走银行家舍入（0.125→0.12），只靠列类型会差一分钱。
CREATE TABLE IF NOT EXISTS "order" (
    id               bigint        NOT NULL,
    created_at       timestamp     NOT NULL,
    updated_at       timestamp     NULL,
    is_deleted       boolean       NOT NULL DEFAULT false,
    deleted_at       timestamp     NULL,
    order_no         varchar(64)   NOT NULL,
    customer_id      bigint        NOT NULL,
    platform_id      bigint        NOT NULL DEFAULT 0,
    merchant_id      bigint        NOT NULL DEFAULT 0,
    -- 10 待支付 / 20 待发货 / 30 待收货 / 40 待取货 / 50 已完成 / 60 已退款 / 91 已取消
    status           int           NOT NULL DEFAULT 10,
    goods_total      numeric(18,2) NOT NULL DEFAULT 0,
    freight          numeric(18,2) NOT NULL DEFAULT 0,
    points_deduction numeric(18,2) NOT NULL DEFAULT 0,
    payable_amount   numeric(18,2) NOT NULL DEFAULT 0,
    points_used      bigint        NOT NULL DEFAULT 0,
    coupon_id        bigint        NOT NULL DEFAULT 0,
    coupon_discount  numeric(18,2) NOT NULL DEFAULT 0,
    receiver_name    varchar(64)   NOT NULL DEFAULT '',
    receiver_phone   varchar(20)   NOT NULL DEFAULT '',
    receiver_address varchar(256)  NOT NULL DEFAULT '',
    idempotency_key  varchar(64)   NOT NULL,
    remark           varchar(512)  NOT NULL DEFAULT '',
    CONSTRAINT pk_order PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uk_order_no ON "order" (order_no) WHERE is_deleted = false;

-- 🔴 幂等键的唯一约束：同一个客户 + 同一个幂等键只允许一张单。
-- 这一条是下单幂等的**最后一道防线**——即使 Redis 锁因为过期或故障没生效，
-- 并发请求也只能有一张单落库，另一张会撞唯一键。
-- 只靠「先查再插」是不够的：两个并发请求可能都查不到然后都插。
CREATE UNIQUE INDEX IF NOT EXISTS uk_order_idempotency
    ON "order" (customer_id, idempotency_key) WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS idx_order_customer ON "order" (customer_id, status, created_at);
CREATE INDEX IF NOT EXISTS idx_order_status ON "order" (status, created_at);

-- 订单行。商品名 / 规格 / 单价都是**快照**：下单之后商品改名或改价不影响这张订单。
-- 订单是对账凭据，显示的必须是当时买的是什么、多少钱。
CREATE TABLE IF NOT EXISTS order_item (
    id                bigint        NOT NULL,
    created_at        timestamp     NOT NULL,
    updated_at        timestamp     NULL,
    is_deleted        boolean       NOT NULL DEFAULT false,
    deleted_at        timestamp     NULL,
    order_id          bigint        NOT NULL,
    order_no          varchar(64)   NOT NULL,
    spu_id            bigint        NOT NULL,
    sku_id            bigint        NOT NULL,
    product_name      varchar(128)  NOT NULL DEFAULT '',
    sku_spec_text     varchar(256)  NOT NULL DEFAULT '',
    price             numeric(18,2) NOT NULL DEFAULT 0,
    quantity          int           NOT NULL DEFAULT 1,
    original_amount   numeric(18,2) NOT NULL DEFAULT 0,
    activity_discount numeric(18,2) NOT NULL DEFAULT 0,
    coupon_discount   numeric(18,2) NOT NULL DEFAULT 0,
    payable_amount    numeric(18,2) NOT NULL DEFAULT 0,
    -- 1 实物快递 / 2 虚拟商品 / 3 实物自提
    delivery_type     int           NOT NULL DEFAULT 1,
    CONSTRAINT pk_order_item PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_order_item_order ON order_item (order_id);
CREATE INDEX IF NOT EXISTS idx_order_item_no ON order_item (order_no);
-- 找出「下单后还没释放库存」的孤儿预留对账用
CREATE INDEX IF NOT EXISTS idx_order_item_sku ON order_item (sku_id);

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;