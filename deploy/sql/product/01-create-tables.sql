-- ProductService 建表脚本（幂等，可重复执行）
-- 依据：DATA_SPEC.md 5.4 分类 / 5.5 品牌 / 5.6 商品 / 5.7 规格与 SKU
--
-- 注意：这里**没有库存表**。库存由 InventoryService 持有（在 simpleshopinventory 库），
-- 商品表里再存一份就成了两份真相。创建商品时的 Stock 只是初始化值。

-- 分类。强制最多三级：level ∈ {1,2,3}
CREATE TABLE IF NOT EXISTS category (
    id            bigint        NOT NULL,
    created_at    timestamp     NOT NULL,
    updated_at    timestamp     NULL,
    is_deleted    boolean       NOT NULL DEFAULT false,
    deleted_at    timestamp     NULL,
    created_by_id bigint       NOT NULL DEFAULT 0,
    created_by_name varchar(64) NOT NULL DEFAULT '',
    operation_id  bigint        NOT NULL DEFAULT 0,
    operation_name varchar(64)  NOT NULL DEFAULT '',
    platform_id   bigint        NOT NULL DEFAULT 0,
    merchant_id   bigint        NOT NULL DEFAULT 0,
    parent_id     bigint        NOT NULL DEFAULT 0,
    category_name varchar(64)   NOT NULL,
    category_code varchar(64)   NOT NULL DEFAULT '',
    icon          varchar(512)  NOT NULL DEFAULT '',
    image         varchar(512)  NOT NULL DEFAULT '',
    sort_order    int           NOT NULL DEFAULT 0,
    level         int           NOT NULL DEFAULT 1,
    status        int           NOT NULL DEFAULT 1,
    CONSTRAINT pk_category PRIMARY KEY (id)
);

-- 同父级内分类名唯一。partial unique index 让「未删除」的约束能用软删语义表达：
-- 用普通 unique 的话，软删掉的同名分类会一直占着坑，新建同名会被误拒。
CREATE UNIQUE INDEX IF NOT EXISTS uk_category_parent_name
    ON category (parent_id, category_name) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS idx_category_parent ON category (parent_id, sort_order);
CREATE INDEX IF NOT EXISTS idx_category_platform ON category (platform_id, status);

-- 品牌。商品的 BrandId 是选填项，所以这张表可以为空（DATA_SPEC 5.5）
CREATE TABLE IF NOT EXISTS brand (
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
    brand_name    varchar(64)   NOT NULL,
    logo          varchar(512)  NOT NULL DEFAULT '',
    sort_order    int           NOT NULL DEFAULT 0,
    status        int           NOT NULL DEFAULT 1,
    CONSTRAINT pk_brand PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uk_brand_name
    ON brand (brand_name) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS idx_brand_platform ON brand (platform_id, status);

-- 商品（SPU）
CREATE TABLE IF NOT EXISTS product (
    id            bigint        NOT NULL,
    created_at    timestamp     NOT NULL,
    updated_at    timestamp     NULL,
    is_deleted    boolean       NOT NULL DEFAULT false,
    deleted_at    timestamp     NULL,
    created_by_id bigint       NOT NULL DEFAULT 0,
    created_by_name varchar(64) NOT NULL DEFAULT '',
    operation_id  bigint        NOT NULL DEFAULT 0,
    operation_name varchar(64)  NOT NULL DEFAULT '',
    platform_id   bigint        NOT NULL DEFAULT 0,
    merchant_id   bigint        NOT NULL DEFAULT 0,
    spu_name      varchar(128)  NOT NULL,
    sub_title     varchar(200)  NOT NULL DEFAULT '',
    brand_id      bigint        NOT NULL DEFAULT 0,
    brand_name    varchar(64)   NOT NULL DEFAULT '',
    category_id   bigint        NOT NULL,
    category_name varchar(64)   NOT NULL DEFAULT '',
    delivery_type int           NOT NULL DEFAULT 1,
    main_image    varchar(512)  NOT NULL DEFAULT '',
    images        varchar(2000) NOT NULL DEFAULT '',
    detail_images varchar(2000) NOT NULL DEFAULT '',
    original_price numeric(18,2) NOT NULL DEFAULT 0,
    min_price     numeric(18,2) NOT NULL DEFAULT 0,
    max_price     numeric(18,2) NOT NULL DEFAULT 0,
    description   varchar(4000) NOT NULL DEFAULT '',
    audit_status  int           NOT NULL DEFAULT 10,
    status        int           NOT NULL DEFAULT 2,
    sales         bigint        NOT NULL DEFAULT 0,
    sort_order    int           NOT NULL DEFAULT 0,
    remark        varchar(512)  NOT NULL DEFAULT '',
    CONSTRAINT pk_product PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_product_category ON product (category_id, status);
CREATE INDEX IF NOT EXISTS idx_product_platform ON product (platform_id, merchant_id, status);
CREATE INDEX IF NOT EXISTS idx_product_audit ON product (audit_status, status);

-- 规格项与规格值：SPU 下动态定义，不建全局字典（DATA_SPEC 5.7.1）
CREATE TABLE IF NOT EXISTS product_spec (
    id         bigint      NOT NULL,
    created_at timestamp   NOT NULL,
    updated_at timestamp   NULL,
    is_deleted boolean     NOT NULL DEFAULT false,
    deleted_at timestamp   NULL,
    product_id bigint      NOT NULL,
    spec_name  varchar(32) NOT NULL,
    sort_order int         NOT NULL DEFAULT 0,
    CONSTRAINT pk_product_spec PRIMARY KEY (id)
);
CREATE INDEX IF NOT EXISTS idx_product_spec_product ON product_spec (product_id, sort_order);

CREATE TABLE IF NOT EXISTS product_spec_value (
    id         bigint      NOT NULL,
    created_at timestamp   NOT NULL,
    updated_at timestamp   NULL,
    is_deleted boolean     NOT NULL DEFAULT false,
    deleted_at timestamp   NULL,
    spec_id    bigint      NOT NULL,
    product_id bigint      NOT NULL,
    value_name varchar(32) NOT NULL,
    sort_order int         NOT NULL DEFAULT 0,
    CONSTRAINT pk_product_spec_value PRIMARY KEY (id)
);
CREATE INDEX IF NOT EXISTS idx_spec_value_spec ON product_spec_value (spec_id, sort_order);
CREATE INDEX IF NOT EXISTS idx_spec_value_product ON product_spec_value (product_id);

-- SKU。注意：**没有 stock 列**，库存在 InventoryService 的库里
CREATE TABLE IF NOT EXISTS sku (
    id            bigint        NOT NULL,
    created_at    timestamp     NOT NULL,
    updated_at    timestamp     NULL,
    is_deleted    boolean       NOT NULL DEFAULT false,
    deleted_at    timestamp     NULL,
    product_id    bigint        NOT NULL,
    sku_code      varchar(64)   NOT NULL,
    sku_name      varchar(256)  NOT NULL DEFAULT '',
    sku_spec_text varchar(256)  NOT NULL DEFAULT '',
    price         numeric(18,2) NOT NULL,
    original_price numeric(18,2) NOT NULL DEFAULT 0,
    image         varchar(512)  NOT NULL DEFAULT '',
    status        int           NOT NULL DEFAULT 1,
    CONSTRAINT pk_sku PRIMARY KEY (id)
);
-- SKU 编码全局唯一，且是 Upsert 的依据
CREATE UNIQUE INDEX IF NOT EXISTS uk_sku_code ON sku (sku_code) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS idx_sku_product ON sku (product_id, status);

-- SKU ↔ 规格值 关联（多对多，复合主键）
CREATE TABLE IF NOT EXISTS sku_spec_value (
    sku_id        bigint      NOT NULL,
    spec_value_id bigint      NOT NULL,
    spec_id       bigint      NOT NULL DEFAULT 0,
    created_at    timestamp   NOT NULL,
    CONSTRAINT pk_sku_spec_value PRIMARY KEY (sku_id, spec_value_id)
);
CREATE INDEX IF NOT EXISTS idx_sku_spec_value_value ON sku_spec_value (spec_value_id);

-- 应用角色授权：simpleshop_app 在 public schema 上没有 CREATE 权限
-- （表结构只由本目录的脚本建立），但需要对已建好的表有 DML 权限。
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;