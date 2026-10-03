-- CartService 建表脚本（幂等，可重复执行）
-- 依据：BUSINESS.md 8.3 购物车、DATA_SPEC 8.4 金额两位小数

CREATE TABLE IF NOT EXISTS cart_item (
    id           bigint        NOT NULL,
    created_at   timestamp     NOT NULL,
    updated_at   timestamp     NULL,
    is_deleted   boolean       NOT NULL DEFAULT false,
    deleted_at   timestamp     NULL,
    customer_id  bigint        NOT NULL,
    sku_id       bigint        NOT NULL,
    product_id   bigint        NOT NULL DEFAULT 0,
    quantity     int           NOT NULL DEFAULT 1,
    -- 以下是**快照**：加购时从商品服务复制一份，之后商品改名 / 改价不影响已有购物车
    sku_name       varchar(256) NOT NULL DEFAULT '',
    sku_spec_text  varchar(256) NOT NULL DEFAULT '',
    price          numeric(18,2) NOT NULL DEFAULT 0,
    original_price numeric(18,2) NOT NULL DEFAULT 0,
    image          varchar(512)  NOT NULL DEFAULT '',
    -- 结算页勾选状态，默认勾选
    checked       boolean       NOT NULL DEFAULT true,
    CONSTRAINT pk_cart_item PRIMARY KEY (id)
);

-- 同一个 SKU 在一个客户的购物车里只有一行：加购是「累加到这一行」而不是插新行。
-- partial unique 让软删的那行不占坑。
CREATE UNIQUE INDEX IF NOT EXISTS uk_cart_customer_sku
    ON cart_item (customer_id, sku_id) WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS idx_cart_customer ON cart_item (customer_id, is_deleted);

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;