-- ToolService 建表脚本（幂等，可重复执行）
CREATE TABLE IF NOT EXISTS stored_file (
    id             bigint        NOT NULL,
    created_at     timestamp     NOT NULL,
    updated_at     timestamp     NULL,
    is_deleted     boolean       NOT NULL DEFAULT false,
    deleted_at     timestamp     NULL,
    original_name  varchar(255)  NOT NULL,
    object_key     varchar(512)  NOT NULL,
    provider       varchar(32)   NOT NULL,
    category       varchar(16)   NOT NULL,
    size_bytes     bigint        NOT NULL,
    extension      varchar(16)   NOT NULL,
    public_url     varchar(1024) NOT NULL,
    CONSTRAINT pk_stored_file PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uk_stored_file_object_key ON stored_file (object_key);
CREATE INDEX IF NOT EXISTS idx_stored_file_category ON stored_file (category, extension);

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;