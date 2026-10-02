-- PermissionService 建表脚本（幂等，可重复执行）
-- 依据 DATA_SPEC.md 2.9：表结构一律由 SQL 脚本管理，不使用 FreeSql CodeFirst。

CREATE TABLE IF NOT EXISTS permission (
    id           bigint       NOT NULL,
    created_at   timestamp    NOT NULL,
    updated_at   timestamp    NULL,
    is_deleted   boolean      NOT NULL DEFAULT false,
    deleted_at   timestamp    NULL,
    name         varchar(64)  NOT NULL,
    code         varchar(64)  NOT NULL,
    api_path     varchar(512) NOT NULL DEFAULT '',
    parent_id    bigint       NOT NULL DEFAULT 0,
    level        integer      NOT NULL DEFAULT 1,
    sort_order   integer      NOT NULL DEFAULT 0,
    status       integer      NOT NULL DEFAULT 1,
    is_builtin   boolean      NOT NULL DEFAULT false,
    description  varchar(200) NOT NULL DEFAULT '',
    CONSTRAINT pk_permission PRIMARY KEY (id)
);

-- 只有叶子（权限点）才有 code，大类与模块是空串。
-- 所以唯一索引必须是部分索引：只约束 code 非空的行，
-- 否则会有 28 行空串互相撞唯一约束。
DROP INDEX IF EXISTS uk_permission_code;
CREATE UNIQUE INDEX IF NOT EXISTS uk_permission_code ON permission (code) WHERE code <> '';
CREATE INDEX IF NOT EXISTS idx_permission_parent ON permission (parent_id, level, sort_order);

CREATE TABLE IF NOT EXISTS role (
    id             bigint       NOT NULL,
    created_at     timestamp    NOT NULL,
    updated_at     timestamp    NULL,
    is_deleted     boolean      NOT NULL DEFAULT false,
    deleted_at     timestamp    NULL,
    role_name      varchar(64)  NOT NULL,
    role_code      varchar(64)  NOT NULL,
    allowed_scopes integer      NOT NULL DEFAULT 1,
    data_scope     integer      NOT NULL DEFAULT 1,
    status         integer      NOT NULL DEFAULT 1,
    is_builtin     boolean      NOT NULL DEFAULT false,
    remark         varchar(512) NOT NULL DEFAULT '',
    CONSTRAINT pk_role PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uk_role_code ON role (role_code);
CREATE UNIQUE INDEX IF NOT EXISTS uk_role_name ON role (role_name);

CREATE TABLE IF NOT EXISTS role_permission (
    role_id       bigint    NOT NULL,
    permission_id bigint    NOT NULL,
    created_at    timestamp NOT NULL,
    CONSTRAINT pk_role_permission PRIMARY KEY (role_id, permission_id)
);

CREATE INDEX IF NOT EXISTS idx_role_permission_perm ON role_permission (permission_id);

CREATE TABLE IF NOT EXISTS user_role (
    user_id     bigint    NOT NULL,
    role_id     bigint    NOT NULL,
    created_at  timestamp NOT NULL,
    platform_id bigint    NOT NULL DEFAULT 0,
    CONSTRAINT pk_user_role PRIMARY KEY (user_id, role_id)
);

CREATE INDEX IF NOT EXISTS idx_user_role_role ON user_role (role_id);
CREATE INDEX IF NOT EXISTS idx_user_role_platform ON user_role (platform_id);

-- 建表是用 postgres 执行的，simpleshop_app 只是数据库级 GRANT，不含表权限，
-- 运行时报 42501 permission denied。三行授权是每个服务的固定约定。
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;

