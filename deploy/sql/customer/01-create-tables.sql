-- CustomerService 建表脚本（幂等，可重复执行）
-- 依据 DATA_SPEC.md 2.9：表结构一律由 SQL 脚本管理，不使用 FreeSql CodeFirst。
-- 时间列统一 timestamp，库级时区已设为 UTC（00-create-databases.sql 第 4 步）。

CREATE TABLE IF NOT EXISTS customer (
    id              bigint       NOT NULL,
    created_at      timestamp    NOT NULL,
    updated_at      timestamp    NULL,
    is_deleted      boolean      NOT NULL DEFAULT false,
    deleted_at      timestamp    NULL,
    customer_name   varchar(64)  NOT NULL,
    password_hash   varchar(256) NOT NULL,
    phone           varchar(20)  NOT NULL,
    nick_name       varchar(64)  NOT NULL DEFAULT '',
    avatar          varchar(512) NOT NULL DEFAULT '',
    gender          integer      NOT NULL DEFAULT 0,
    birthday        date         NULL,
    last_login_at   timestamp    NULL,
    CONSTRAINT pk_customer PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uk_customer_name ON customer (customer_name);
CREATE UNIQUE INDEX IF NOT EXISTS uk_customer_phone ON customer (phone);

COMMENT ON TABLE  customer            IS '前台客户账号（DATA_SPEC 2.6）';
COMMENT ON COLUMN customer.password_hash IS '只存哈希，禁止明文';
COMMENT ON COLUMN customer.gender        IS '0 未知 / 1 男 / 2 女';

CREATE TABLE IF NOT EXISTS customer_address (
    id              bigint       NOT NULL,
    created_at      timestamp    NOT NULL,
    updated_at      timestamp    NULL,
    is_deleted      boolean      NOT NULL DEFAULT false,
    deleted_at      timestamp    NULL,
    customer_id     bigint       NOT NULL,
    customer_name   varchar(64)  NOT NULL DEFAULT '',
    consignee_name  varchar(64)  NOT NULL,
    consignee_phone varchar(20)  NOT NULL,
    province_code   varchar(12)  NOT NULL DEFAULT '',
    city_code       varchar(12)  NOT NULL DEFAULT '',
    district_code   varchar(12)  NOT NULL DEFAULT '',
    region_path     varchar(255) NOT NULL DEFAULT '',
    detail_address  varchar(255) NOT NULL,
    is_default      boolean      NOT NULL DEFAULT false,
    label           varchar(16)  NOT NULL DEFAULT '',
    CONSTRAINT pk_customer_address PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_customer_address_customer
    ON customer_address (customer_id, is_deleted);

-- 同一客户至多一条默认地址：部分唯一索引，软删后不再占用
CREATE UNIQUE INDEX IF NOT EXISTS uk_customer_address_default
    ON customer_address (customer_id) WHERE is_default = true AND is_deleted = false;

COMMENT ON TABLE customer_address IS '客户收货地址（DATA_SPEC 2.3）';
COMMENT ON COLUMN customer_address.region_path IS '省市区名称路径，斜杠分隔，列表直接展示';

CREATE TABLE IF NOT EXISTS customer_favorite (
    id              bigint       NOT NULL,
    created_at      timestamp    NOT NULL,
    updated_at      timestamp    NULL,
    is_deleted      boolean      NOT NULL DEFAULT false,
    deleted_at      timestamp    NULL,
    customer_id     bigint       NOT NULL,
    customer_name   varchar(64)  NOT NULL DEFAULT '',
    spu_id          bigint       NOT NULL,
    favorited_at    timestamp    NOT NULL,
    CONSTRAINT pk_customer_favorite PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_customer_favorite_customer
    ON customer_favorite (customer_id, is_deleted, favorited_at DESC);

-- 同一客户对同一 SPU 只保留一条有效收藏
CREATE UNIQUE INDEX IF NOT EXISTS uk_customer_favorite_spu
    ON customer_favorite (customer_id, spu_id) WHERE is_deleted = false;

COMMENT ON TABLE customer_favorite IS '客户收藏（REVIEW P2 风险 20：单客户上限 20）';

-- ============================================================================
-- 授权：建表是用 postgres 执行的，simpleshop_app 只是数据库级 GRANT，
-- 不含表权限，运行时会报 42501 permission denied for table。
-- 每个服务的建表脚本末尾都要有这三行（这是约定，见 PLAN.md 13 全局约束）。
-- ALTER DEFAULT PRIVILEGES 保证后续新增的表也自动授权。
-- ============================================================================
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;

