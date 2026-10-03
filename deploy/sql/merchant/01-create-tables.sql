-- MerchantPlatformService 建表脚本（幂等，可重复执行）
-- 依据：BUSINESS.md 1.4 可见性、16 装修；DATA_SPEC 5.1 平台、5.2 商户、5.3 审核、5.31 地区地址

-- ===== 平台 =====
CREATE TABLE IF NOT EXISTS platform (
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
    platform_name    varchar(128)  NOT NULL DEFAULT '',
    -- 6 位字母，全局唯一。**编辑时只读**：小程序用 PLATFORM_CODE 锁死它，
    -- 改了等于让已发布的小程序找不到对应平台
    platform_code    varchar(16)   NOT NULL DEFAULT '',
    contact_name     varchar(64)   NOT NULL DEFAULT '',
    contact_phone    varchar(20)   NOT NULL DEFAULT '',
    logo             varchar(512)  NOT NULL DEFAULT '',
    mall_name        varchar(128)  NOT NULL DEFAULT '',
    notice           varchar(500)  NOT NULL DEFAULT '',
    -- 三档主题色（用户明确要求的三档：主题色 / TabBar 选中色 / 页面背景色）
    primary_color    varchar(16)   NOT NULL DEFAULT '#0071e3',
    tab_color        varchar(16)   NOT NULL DEFAULT '#0071e3',
    background_color varchar(16)   NOT NULL DEFAULT '#f5f5f7',
    -- 运费仅对实物快递收取；FreeShippingThreshold = 0 表示不启用包邮
    shipping_fee             numeric(18,2) NOT NULL DEFAULT 0,
    free_shipping_threshold  numeric(18,2) NOT NULL DEFAULT 0,
    status          int           NOT NULL DEFAULT 1,
    remark          varchar(512)  NOT NULL DEFAULT '',
    CONSTRAINT pk_platform PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uk_platform_name
    ON platform (platform_name) WHERE is_deleted = false;
CREATE UNIQUE INDEX IF NOT EXISTS uk_platform_code
    ON platform (platform_code) WHERE is_deleted = false;

-- ===== 商户 =====
CREATE TABLE IF NOT EXISTS merchant (
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
    merchant_name    varchar(128)  NOT NULL DEFAULT '',
    -- 商户编号 = 平台编码 + 雪花 Id，如 DEMOPL13755080881608709。**系统生成，用户不填**
    merchant_no      varchar(64)   NOT NULL DEFAULT '',
    contact_name     varchar(64)   NOT NULL DEFAULT '',
    contact_phone    varchar(20)   NOT NULL DEFAULT '',
    logo             varchar(512)  NOT NULL DEFAULT '',
    description      varchar(1000) NOT NULL DEFAULT '',
    -- 新建默认停用：必须审核通过后才能运营（规格 5.2）
    status           int           NOT NULL DEFAULT 2,
    -- 审核状态：10 待审核 / 20 已通过 / 90 已拒绝。新建固定 10
    audit_status     int           NOT NULL DEFAULT 10,
    audit_remark     varchar(500)  NOT NULL DEFAULT '',
    audited_at       timestamp     NULL,
    auditor_id       bigint        NOT NULL DEFAULT 0,
    auditor_name     varchar(64)   NOT NULL DEFAULT '',
    -- 店铺评分（冗余）。只统计**有评价商品**的均分平均值，
    -- 零评价商品的默认 5.0 算进去会让评分虚高（规格 14.5）
    rating           numeric(3,2)  NOT NULL DEFAULT 0,
    remark           varchar(512)  NOT NULL DEFAULT '',
    CONSTRAINT pk_merchant PRIMARY KEY (id)
);

-- 同平台内商户名唯一（不同平台可以有同名店铺）
CREATE UNIQUE INDEX IF NOT EXISTS uk_merchant_platform_name
    ON merchant (platform_id, merchant_name) WHERE is_deleted = false;

CREATE UNIQUE INDEX IF NOT EXISTS uk_merchant_no
    ON merchant (merchant_no) WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS idx_merchant_platform ON merchant (platform_id, status, audit_status);
CREATE INDEX IF NOT EXISTS idx_merchant_audit ON merchant (audit_status, created_at DESC);

-- ===== 地区地址配置 =====
-- 每个平台一份。RegionsJson 为空 = 用内置默认地区库（规格 5.31）
CREATE TABLE IF NOT EXISTS platform_config (
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
    -- 三级地区数据的 JSON 数组，上限 2MB。为空字符串表示「回落内置默认」
    regions_json     text          NOT NULL DEFAULT '',
    CONSTRAINT pk_platform_config PRIMARY KEY (id)
);

-- 每个平台只有一份配置
CREATE UNIQUE INDEX IF NOT EXISTS uk_platform_config_platform
    ON platform_config (platform_id) WHERE is_deleted = false;

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;
