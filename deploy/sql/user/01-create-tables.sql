-- UserService 建表脚本（幂等，可重复执行）
-- 依据 DATA_SPEC.md 2.9：表结构一律由 SQL 脚本管理，不使用 FreeSql CodeFirst。
--
-- 表名用 app_user 而不是 user：user 在 PostgreSQL 里是保留字相关的常用名，
-- 且与 CustomerService 的 customer 表区分开更清楚。

CREATE TABLE IF NOT EXISTS app_user (
    id             bigint       NOT NULL,
    created_at     timestamp    NOT NULL,
    updated_at     timestamp    NULL,
    is_deleted     boolean      NOT NULL DEFAULT false,
    deleted_at     timestamp    NULL,
    user_name      varchar(64)  NOT NULL,
    password_hash  varchar(256) NOT NULL,
    phone          varchar(20)  NOT NULL,
    email          varchar(128) NOT NULL DEFAULT '',
    nick_name      varchar(64)  NOT NULL DEFAULT '',
    avatar         varchar(512) NOT NULL DEFAULT '',
    tenant_type    integer      NOT NULL,
    platform_id    bigint       NOT NULL DEFAULT 0,
    merchant_id    bigint       NOT NULL DEFAULT 0,
    status         integer      NOT NULL DEFAULT 1,
    last_login_at  timestamp    NULL,
    CONSTRAINT pk_app_user PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uk_app_user_name ON app_user (user_name);
CREATE UNIQUE INDEX IF NOT EXISTS uk_app_user_phone ON app_user (phone);
CREATE INDEX IF NOT EXISTS idx_app_user_tenant ON app_user (tenant_type, platform_id, merchant_id);
CREATE INDEX IF NOT EXISTS idx_app_user_status ON app_user (status);

-- 建表是用 postgres 执行的，simpleshop_app 只是数据库级 GRANT，不含表权限。
-- 这三行是每个服务建表脚本的固定约定。
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;