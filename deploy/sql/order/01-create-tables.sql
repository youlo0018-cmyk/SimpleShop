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
    customer_no      varchar(64)   NOT NULL DEFAULT '',
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
    -- 发货内容 / 发货备注。客户下单备注在 remark；这里存的是**商户发货时写的内容**：
    -- 虚拟商品是卡号 / 激活码（展示给客户，空着发货等于顾客付了钱什么都拿不到），
    -- 快递是发货备注（选填）。与 remark 分开是为了两者互不覆盖。
    ship_remark      varchar(512)  NOT NULL DEFAULT '',
    -- 🔴 支付时间与完成时间是**报表口径的分水岭**，不能拿 created_at 代替。
    -- GMV 要的是「这段时间里收了多少钱」，按下单时间算会把「昨天下单今天付」
    -- 算进昨天，而昨天的日报里这笔钱根本没收过——对账时对不上。
    paid_at          timestamp     NULL,
    completed_at     timestamp     NULL,
    CONSTRAINT pk_order PRIMARY KEY (id)
);

ALTER TABLE "order" ADD COLUMN IF NOT EXISTS customer_no varchar(64) NOT NULL DEFAULT '';

-- 🔴 这两列必须放在**建索引之前**。CREATE TABLE IF NOT EXISTS 对已存在的表是空操作，
-- 老环境上 paid_at 并不存在；而下面 ALTER 才补它。
-- 先建索引再补列的话，老环境会直接报 `column "paid_at" does not exist`，
-- 整个建表脚本失败——而且报错完全指不到真正原因（看起来像索引写错了）。
ALTER TABLE "order" ADD COLUMN IF NOT EXISTS paid_at timestamp NULL;
ALTER TABLE "order" ADD COLUMN IF NOT EXISTS completed_at timestamp NULL;

-- 物流信息。发货时必填物流公司 + 单号（用户 2026-10 需求：发货要选物流公司并填单号）。
-- company_name 是**快照**：物流公司表里的名字之后可能被改或删，
-- 只存 Id 的话半年后回查这张单会指向另一家公司，对账时对不上。
-- 同样不能只靠 status>=30 反推发货时间：当天买次日发是常态，用下单时间算出来的时效会差一天。
ALTER TABLE "order" ADD COLUMN IF NOT EXISTS logistics_company_id bigint NOT NULL DEFAULT 0;
ALTER TABLE "order" ADD COLUMN IF NOT EXISTS logistics_company_name varchar(128) NOT NULL DEFAULT '';
ALTER TABLE "order" ADD COLUMN IF NOT EXISTS tracking_no varchar(64) NOT NULL DEFAULT '';
ALTER TABLE "order" ADD COLUMN IF NOT EXISTS shipped_at timestamp NULL;

-- 发货内容 / 发货备注。老环境补列用 IF NOT EXISTS，重复执行安全。
ALTER TABLE "order" ADD COLUMN IF NOT EXISTS ship_remark varchar(512) NOT NULL DEFAULT '';

-- 已退金额合计（冗余列，见 Order.RefundedAmount 注释）。
-- 它必须与订单状态在**同一条 UPDATE** 里累加，才能在并发退款下挡住超退；
-- 每次去 order_refund_item 上 SUM 的话，两个并发请求会读到同一个旧值，
-- 都判断「还有余额」，然后一起把订单退成超额。
ALTER TABLE "order" ADD COLUMN IF NOT EXISTS refunded_amount numeric(18,2) NOT NULL DEFAULT 0;

CREATE UNIQUE INDEX IF NOT EXISTS uk_order_no ON "order" (order_no) WHERE is_deleted = false;

-- 🔴 幂等键的唯一约束：同一个客户 + 同一个幂等键只允许一张单。
-- 这一条是下单幂等的**最后一道防线**——即使 Redis 锁因为过期或故障没生效，
-- 并发请求也只能有一张单落库，另一张会撞唯一键。
-- 只靠「先查再插」是不够的：两个并发请求可能都查不到然后都插。
CREATE UNIQUE INDEX IF NOT EXISTS uk_order_idempotency
    ON "order" (customer_id, idempotency_key) WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS idx_order_customer ON "order" (customer_id, status, created_at);
CREATE INDEX IF NOT EXISTS idx_order_customer_no ON "order" (customer_no, created_at);
CREATE INDEX IF NOT EXISTS idx_order_status ON "order" (status, created_at);

-- 报表按「支付时间 + 商户」过滤用。没有这个索引，近 30 天报表会全表扫。
CREATE INDEX IF NOT EXISTS idx_order_paid_at ON "order" (paid_at, merchant_id);

-- 按物流单号反查订单：客服收到用户的物流截图时，第一件事就是拿单号搜。
-- 没有这个索引就是全表扫，而订单表是全系统增长最快的一张。
CREATE INDEX IF NOT EXISTS idx_order_tracking_no ON "order" (tracking_no) WHERE is_deleted = false;

-- 订单退款记录。一张订单可以有多条（多次部分退款），不是一条订单只能退一次。
CREATE TABLE IF NOT EXISTS order_refund (
    id              bigint        NOT NULL,
    created_at      timestamp     NOT NULL,
    updated_at      timestamp     NULL,
    is_deleted      boolean       NOT NULL DEFAULT false,
    deleted_at      timestamp     NULL,
    refund_no       varchar(32)   NOT NULL,
    order_id        bigint        NOT NULL,
    order_no        varchar(64)   NOT NULL,
    platform_id     bigint        NOT NULL DEFAULT 0,
    merchant_id     bigint        NOT NULL DEFAULT 0,
    customer_id     bigint        NOT NULL DEFAULT 0,
    amount          numeric(18,2) NOT NULL DEFAULT 0,
    -- 1 部分退款 / 2 整单退款
    refund_type     int           NOT NULL DEFAULT 1,
    fully_refunded  boolean       NOT NULL DEFAULT false,
    reason          varchar(512)  NOT NULL DEFAULT '',
    operator_id     bigint        NOT NULL DEFAULT 0,
    operator_name   varchar(64)   NOT NULL DEFAULT '',
    CONSTRAINT pk_order_refund PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uk_order_refund_no ON order_refund (refund_no) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS idx_order_refund_order ON order_refund (order_id, created_at);

-- 退款明细（按订单行退）。部分退款按行定位，行级的可退余额从这里 SUM 出来。
CREATE TABLE IF NOT EXISTS order_refund_item (
    id              bigint        NOT NULL,
    created_at      timestamp     NOT NULL,
    updated_at      timestamp     NULL,
    is_deleted      boolean       NOT NULL DEFAULT false,
    deleted_at      timestamp     NULL,
    refund_id       bigint        NOT NULL,
    order_id        bigint        NOT NULL,
    order_item_id   bigint        NOT NULL,
    sku_id          bigint        NOT NULL,
    product_name    varchar(128)  NOT NULL DEFAULT '',
    sku_spec_text   varchar(256)  NOT NULL DEFAULT '',
    quantity        int           NOT NULL DEFAULT 1,
    amount          numeric(18,2) NOT NULL DEFAULT 0,
    CONSTRAINT pk_order_refund_item PRIMARY KEY (id)
);

-- 行级余额校验每次都要按 order_item_id 聚合，没有这个索引就是全表扫，
-- 而订单明细表是随订单量线性增长的。
CREATE INDEX IF NOT EXISTS idx_order_refund_item_order_item
    ON order_refund_item (order_item_id) WHERE is_deleted = false;

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

-- 订单行来源：1 普通 / 2 秒杀。
-- 秒杀行的库存在**发布场次时**就从常规池划走了，下单与支付都不能再动常规库存；
-- 没有这个标记，秒杀单会在下单时再锁一次常规库存，直接超卖。
ALTER TABLE order_item ADD COLUMN IF NOT EXISTS source_type int NOT NULL DEFAULT 1;

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;
