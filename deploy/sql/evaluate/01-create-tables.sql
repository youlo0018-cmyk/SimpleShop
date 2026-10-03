-- EvaluateService 建表脚本（幂等，可重复执行）
-- 依据：BUSINESS.md 14 评价、DATA_SPEC 5.27 评价管理

-- ===== 首评：粒度是 SPU，一条订单内同一 SPU 只能有一条 =====
CREATE TABLE IF NOT EXISTS evaluate (
    id           bigint        NOT NULL,
    created_at   timestamp     NOT NULL,
    updated_at   timestamp     NULL,
    is_deleted   boolean       NOT NULL DEFAULT false,
    deleted_at   timestamp     NULL,
    -- CustomerEntityBase：客户 AOP 按 customer_id 自动过滤
    customer_id     bigint      NOT NULL,
    customer_name   varchar(64) NOT NULL DEFAULT '',
    -- 冗余自商品，只作后台查询维度（不参与 AOP 隔离，见 EvaluateEntities 注释）
    platform_id     bigint      NOT NULL DEFAULT 0,
    merchant_id     bigint      NOT NULL DEFAULT 0,
    spu_id          bigint      NOT NULL,
    -- 快照：商品改名 / 下架 / 删除后评价仍要展示，联表会让历史评价「改口」
    spu_name        varchar(128) NOT NULL DEFAULT '',
    -- 本订单实际购买的规格名清单，逗号分隔，最多列 3 个后折叠为「等 N 个规格」
    sku_specs       varchar(512) NOT NULL DEFAULT '',
    -- 幂等键 {order_no}:{spu_id} 的载体
    order_no        varchar(64)  NOT NULL DEFAULT '',
    order_id        bigint       NOT NULL DEFAULT 0,
    star_score      int          NOT NULL DEFAULT 5,
    content         varchar(1000) NOT NULL DEFAULT '',
    images          varchar(2048) NOT NULL DEFAULT '',
    is_anonymous    boolean      NOT NULL DEFAULT false,
    -- 后台隐藏：软删 + 原因，数据保留，C 端不展示
    is_hidden       boolean      NOT NULL DEFAULT false,
    hidden_reason   varchar(500) NOT NULL DEFAULT '',
    hidden_at       timestamp    NULL,
    hidden_by_id    bigint       NOT NULL DEFAULT 0,
    CONSTRAINT pk_evaluate PRIMARY KEY (id)
);

-- 🔴 幂等的**最后防线**（规格 14.1：一个订单内同一 SPU 只能有一条首评）。
-- 即使买了该 SPU 的多个 SKU、或分多次购买，也只评一次。
-- 靠唯一索引而不是「先查再插」：两个并发提交都可能查到「还没评过」。
CREATE UNIQUE INDEX IF NOT EXISTS uk_evaluate_order_spu
    ON evaluate (order_no, spu_id) WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS idx_evaluate_spu ON evaluate (spu_id, is_hidden, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_evaluate_customer ON evaluate (customer_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_evaluate_merchant ON evaluate (merchant_id, created_at DESC);

-- ===== SKU 标记：详情页按当前选中 SKU 过滤评价 =====
CREATE TABLE IF NOT EXISTS evaluate_sku_ref (
    id             bigint       NOT NULL,
    created_at     timestamp    NOT NULL,
    updated_at     timestamp    NULL,
    is_deleted     boolean      NOT NULL DEFAULT false,
    deleted_at     timestamp    NULL,
    evaluate_id    bigint       NOT NULL,
    sku_id         bigint       NOT NULL,
    sku_spec_text  varchar(256) NOT NULL DEFAULT '',
    -- 保留它是为了能反查「这条评价对应订单里哪一行」，后台核对时能对上原始金额
    order_item_id  bigint       NOT NULL DEFAULT 0,
    CONSTRAINT pk_evaluate_sku_ref PRIMARY KEY (id)
);

-- 一条评价对同一 SKU 只能有一条标记
CREATE UNIQUE INDEX IF NOT EXISTS uk_evaluate_sku_ref
    ON evaluate_sku_ref (evaluate_id, sku_id) WHERE is_deleted = false;

-- 详情页过滤走的就是这条索引：按 sku_id 找评价
CREATE INDEX IF NOT EXISTS idx_evaluate_sku_ref_sku ON evaluate_sku_ref (sku_id, evaluate_id);

-- ===== 追评：挂在首评下方，最多 3 条，不单独计入均分 =====
CREATE TABLE IF NOT EXISTS evaluate_append (
    id            bigint        NOT NULL,
    created_at    timestamp     NOT NULL,
    updated_at    timestamp     NULL,
    is_deleted    boolean       NOT NULL DEFAULT false,
    deleted_at    timestamp     NULL,
    customer_id     bigint      NOT NULL,
    customer_name   varchar(64) NOT NULL DEFAULT '',
    evaluate_id    bigint       NOT NULL,
    content        varchar(1000) NOT NULL DEFAULT '',
    images         varchar(2048) NOT NULL DEFAULT '',
    -- 存了但不参与均分（规格 14.2），只用于展示「追评时改成了几星」
    star_score     int          NOT NULL DEFAULT 0,
    CONSTRAINT pk_evaluate_append PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_evaluate_append_eval ON evaluate_append (evaluate_id, created_at);

-- ===== 回复：商户与平台各可回复 1 次，不可编辑只能追加 =====
CREATE TABLE IF NOT EXISTS evaluate_reply (
    id             bigint        NOT NULL,
    created_at     timestamp     NOT NULL,
    updated_at     timestamp     NULL,
    is_deleted     boolean       NOT NULL DEFAULT false,
    deleted_at     timestamp     NULL,
    evaluate_id    bigint        NOT NULL,
    -- 0 表示回复首评，非 0 表示回复某条追评
    append_id      bigint        NOT NULL DEFAULT 0,
    content        varchar(1000) NOT NULL DEFAULT '',
    -- 1 商户回复 / 2 平台回复
    reply_type     int           NOT NULL DEFAULT 1,
    reply_by_id    bigint        NOT NULL DEFAULT 0,
    reply_by_name  varchar(64)   NOT NULL DEFAULT '',
    CONSTRAINT pk_evaluate_reply PRIMARY KEY (id)
);

-- 🔴 「每个主体对同一条评价（含追评）只能回复 1 次」的最终防线（规格 14.3）。
-- 只靠「先查有没有回过」在并发下必然漏，所以用唯一索引。
CREATE UNIQUE INDEX IF NOT EXISTS uk_evaluate_reply_once
    ON evaluate_reply (evaluate_id, append_id, reply_type) WHERE is_deleted = false;

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;
