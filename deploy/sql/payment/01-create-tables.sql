-- PaymentService 建表脚本（幂等，可重复执行）
-- 依据：BUSINESS.md 10 支付与退款；DATA_SPEC 5.25 退款审批、5.26 代客申请退款、5.28 模拟支付

-- ===== 支付单 =====
CREATE TABLE IF NOT EXISTS payment_order (
    id               bigint        NOT NULL,
    created_at       timestamp     NOT NULL,
    updated_at       timestamp     NULL,
    is_deleted       boolean       NOT NULL DEFAULT false,
    deleted_at       timestamp     NULL,
    created_by_id    bigint        NOT NULL DEFAULT 0,
    created_by_name  varchar(64)   NOT NULL DEFAULT '',
    operation_id     bigint        NOT NULL DEFAULT 0,
    operation_name   varchar(64)   NOT NULL DEFAULT '',
    platform_id      bigint        NOT NULL DEFAULT 0,
    merchant_id      bigint        NOT NULL DEFAULT 0,
    -- 支付单号，业务唯一
    payment_no       varchar(32)   NOT NULL DEFAULT '',
    order_id         bigint        NOT NULL DEFAULT 0,
    order_no         varchar(64)   NOT NULL DEFAULT '',
    -- 🔴 金额一律**服务端反查订单实付**（规格 10.1），不接受客户端传入。
    -- 这里存的是下单那一刻的实付快照；订单后来改价也不影响已生成的支付单
    amount           numeric(18,2) NOT NULL DEFAULT 0,
    -- 1 待支付 / 20 已支付 / 30 已关闭（超时）
    status           int           NOT NULL DEFAULT 1,
    channel          int           NOT NULL DEFAULT 1,
    paid_at          timestamp     NULL,
    closed_at        timestamp     NULL,
    -- 模拟支付失败原因（成功时为空）
    fail_reason      varchar(200)  NOT NULL DEFAULT '',
    remark           varchar(512)  NOT NULL DEFAULT '',
    CONSTRAINT pk_payment_order PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uk_payment_no
    ON payment_order (payment_no) WHERE is_deleted = false;

-- 幂等键 {order_no}:{channel}。一个订单同一通道只允许一张支付单，
-- 用户反复点「去支付」不会造出一堆单子
CREATE UNIQUE INDEX IF NOT EXISTS uk_payment_biz
    ON payment_order (order_no, channel) WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS idx_payment_order_no ON payment_order (order_no);

-- ===== 退款单 =====
CREATE TABLE IF NOT EXISTS refund_order (
    id               bigint        NOT NULL,
    created_at       timestamp     NOT NULL,
    updated_at       timestamp     NULL,
    is_deleted       boolean       NOT NULL DEFAULT false,
    deleted_at       timestamp     NULL,
    created_by_id    bigint        NOT NULL DEFAULT 0,
    created_by_name  varchar(64)   NOT NULL DEFAULT '',
    operation_id     bigint        NOT NULL DEFAULT 0,
    operation_name   varchar(64)   NOT NULL DEFAULT '',
    platform_id      bigint        NOT NULL DEFAULT 0,
    merchant_id      bigint        NOT NULL DEFAULT 0,
    refund_no        varchar(32)   NOT NULL DEFAULT '',
    order_id         bigint        NOT NULL DEFAULT 0,
    order_no         varchar(64)   NOT NULL DEFAULT '',
    customer_id      bigint        NOT NULL DEFAULT 0,
    customer_name    varchar(64)   NOT NULL DEFAULT '',
    -- 申请金额。本次退款的合计（各行之和）
    amount           numeric(18,2) NOT NULL DEFAULT 0,
    -- 1 整单退 / 2 部分退。运费：整单退含运费，部分退**不退运费**（规格 10.2）
    refund_type      int           NOT NULL DEFAULT 1,
    -- 10 待审批 / 20 已退款 / 90 已拒绝
    status           int           NOT NULL DEFAULT 10,
    reason           varchar(500)  NOT NULL DEFAULT '',
    reject_reason    varchar(500)  NOT NULL DEFAULT '',
    approver_id      bigint        NOT NULL DEFAULT 0,
    approver_name    varchar(64)   NOT NULL DEFAULT '',
    approved_at      timestamp     NULL,
    CONSTRAINT pk_refund_order PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uk_refund_no
    ON refund_order (refund_no) WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS idx_refund_order ON refund_order (order_no, status);
CREATE INDEX IF NOT EXISTS idx_refund_customer ON refund_order (customer_id, created_at DESC);

-- ===== 退款单明细：按订单行退 =====
CREATE TABLE IF NOT EXISTS refund_order_item (
    id               bigint        NOT NULL,
    created_at       timestamp     NOT NULL,
    updated_at       timestamp     NULL,
    is_deleted       boolean       NOT NULL DEFAULT false,
    deleted_at       timestamp     NULL,
    created_by_id    bigint        NOT NULL DEFAULT 0,
    created_by_name  varchar(64)   NOT NULL DEFAULT '',
    operation_id     bigint        NOT NULL DEFAULT 0,
    operation_name   varchar(64)   NOT NULL DEFAULT '',
    platform_id      bigint        NOT NULL DEFAULT 0,
    merchant_id      bigint        NOT NULL DEFAULT 0,
    refund_id        bigint        NOT NULL,
    order_item_id    bigint        NOT NULL,
    sku_id           bigint        NOT NULL DEFAULT 0,
    -- 商品名与规格是**快照**：商品改名后历史退款单要显示当时的名字
    product_name     varchar(128)  NOT NULL DEFAULT '',
    sku_spec_text    varchar(256)  NOT NULL DEFAULT '',
    quantity         int           NOT NULL DEFAULT 0,
    amount           numeric(18,2) NOT NULL DEFAULT 0,
    CONSTRAINT pk_refund_order_item PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uk_refund_item
    ON refund_order_item (refund_id, order_item_id) WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS idx_refund_item_order ON refund_order_item (order_item_id);

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;
