-- InventoryService 建表脚本（幂等，可重复执行）
-- 依据：BUSINESS.md 9（库存模型 / 操作语义 / 约束）、DATA_SPEC.md 5.10（库存管理）
--
-- 三个计数各自独立，不能合并成一个「库存」：
--   available 可用（能被锁定）  locked 锁定（下单占用，未支付）  deducted 已扣减（已支付）
-- 合并之后就分不清「没卖掉」和「卖掉但没发货」，退款与对账全都没法做。

-- 库存。SKU 维度，一个 SKU 一行。
CREATE TABLE IF NOT EXISTS stock (
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
    sku_id        bigint        NOT NULL,
    product_name  varchar(128)  NOT NULL DEFAULT '',
    sku_spec_text varchar(256)  NOT NULL DEFAULT '',
    available     int           NOT NULL DEFAULT 0,
    locked        int           NOT NULL DEFAULT 0,
    deducted      int           NOT NULL DEFAULT 0,
    warn_threshold int          NOT NULL DEFAULT 0,
    CONSTRAINT pk_stock PRIMARY KEY (id)
);

-- 一个 SKU 只能有一条未删除的库存记录。
-- partial unique：软删掉的那条不该继续占着坑，否则重建会撞唯一键。
CREATE UNIQUE INDEX IF NOT EXISTS uk_stock_sku ON stock (sku_id) WHERE is_deleted = false;

-- ============================================================
-- 库存流水。**幂等就靠这张表**，不是靠应用层的判断。
-- ============================================================
CREATE TABLE IF NOT EXISTS stock_flow (
    id            bigint        NOT NULL,
    created_at    timestamp     NOT NULL,
    updated_at    timestamp     NULL,
    is_deleted    boolean       NOT NULL DEFAULT false,
    deleted_at    timestamp     NULL,
    biz_no        varchar(64)   NOT NULL,
    sku_id        bigint        NOT NULL,
    action        varchar(32)   NOT NULL,
    quantity      int           NOT NULL,
    before_available int        NOT NULL DEFAULT 0,
    after_available  int        NOT NULL DEFAULT 0,
    before_locked    int        NOT NULL DEFAULT 0,
    after_locked     int        NOT NULL DEFAULT 0,
    before_deducted  int        NOT NULL DEFAULT 0,
    after_deducted   int        NOT NULL DEFAULT 0,
    remark        varchar(512)  NOT NULL DEFAULT '',
    operation_id  bigint        NOT NULL DEFAULT 0,
    operation_name varchar(64)  NOT NULL DEFAULT '',
    platform_id   bigint        NOT NULL DEFAULT 0,
    merchant_id   bigint        NOT NULL DEFAULT 0,
    CONSTRAINT pk_stock_flow PRIMARY KEY (id)
);

-- 🔴 幂等键：biz_no + sku_id + action。
-- 重复请求会撞这个唯一键，插入失败 → 识别为「已经处理过」，直接返回首次结果。
-- 必须是数据库约束而不是「先查再插」：两个并发请求可能都查不到，然后都插入。
CREATE UNIQUE INDEX IF NOT EXISTS uk_stock_flow_idem
    ON stock_flow (biz_no, sku_id, action) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS idx_stock_flow_sku ON stock_flow (sku_id, created_at);
CREATE INDEX IF NOT EXISTS idx_stock_flow_biz ON stock_flow (biz_no);

-- ============================================================
-- 补偿表：释放失败时写这里，由 ScheduledService 每轮重试。
-- ============================================================
CREATE TABLE IF NOT EXISTS pending_stock_release (
    id            bigint        NOT NULL,
    created_at    timestamp     NOT NULL,
    updated_at    timestamp     NULL,
    is_deleted    boolean       NOT NULL DEFAULT false,
    deleted_at    timestamp     NULL,
    biz_no        varchar(64)   NOT NULL,
    sku_id        bigint        NOT NULL,
    quantity      int           NOT NULL,
    reason        varchar(512)  NOT NULL DEFAULT '',
    status        int           NOT NULL DEFAULT 0,
    retry_count   int           NOT NULL DEFAULT 0,
    last_error    varchar(512)  NOT NULL DEFAULT '',
    next_retry_at timestamp     NOT NULL DEFAULT now(),
    platform_id   bigint        NOT NULL DEFAULT 0,
    merchant_id   bigint        NOT NULL DEFAULT 0,
    CONSTRAINT pk_pending_stock_release PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_pending_status ON pending_stock_release (status, next_retry_at);

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;