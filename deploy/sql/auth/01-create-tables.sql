-- AuthService 建表脚本（幂等，可重复执行）
--
-- 这份 SQL 是 **由 EF Core 迁移生成后固化下来的**，不要手改：
--   dotnet ef migrations add <Name> --output-dir Migrations \
--     --project src/AuthService/AuthService.Infrastructure \
--     --startup-project src/AuthService/AuthService.Api
--   dotnet ef migrations script --idempotent ... --output deploy/sql/auth/01-create-tables.sql
-- 改 OpenIddict 版本或实体后要重新生成，别直接编辑本文件（DATA_SPEC 2.9：表结构由 SQL 脚本管理，运行期不用 CodeFirst）。
--
-- 库里的表全是 OpenIddict 自己的（应用 / 授权 / 作用域 / 令牌）。
-- 这里**没有也不该有**任何业务表：后台账号在 simpleshopuser，角色权限在 simpleshoppermission。
-- 认证服务只管令牌生命周期，不持有业务数据，更不持有密码哈希。
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261002203924_InitialOpenIddictSchema') THEN
    CREATE TABLE "OpenIddictApplications" (
        "Id" text NOT NULL,
        "ApplicationType" character varying(50),
        "ClientId" character varying(100),
        "ClientSecret" text,
        "ClientType" character varying(50),
        "ConcurrencyToken" character varying(50),
        "ConsentType" character varying(50),
        "DisplayName" text,
        "DisplayNames" text,
        "JsonWebKeySet" text,
        "Permissions" text,
        "PostLogoutRedirectUris" text,
        "Properties" text,
        "RedirectUris" text,
        "Requirements" text,
        "Settings" text,
        CONSTRAINT "PK_OpenIddictApplications" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261002203924_InitialOpenIddictSchema') THEN
    CREATE TABLE "OpenIddictScopes" (
        "Id" text NOT NULL,
        "ConcurrencyToken" character varying(50),
        "Description" text,
        "Descriptions" text,
        "DisplayName" text,
        "DisplayNames" text,
        "Name" character varying(200),
        "Properties" text,
        "Resources" text,
        CONSTRAINT "PK_OpenIddictScopes" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261002203924_InitialOpenIddictSchema') THEN
    CREATE TABLE "OpenIddictAuthorizations" (
        "Id" text NOT NULL,
        "ApplicationId" text,
        "ConcurrencyToken" character varying(50),
        "CreationDate" timestamp with time zone,
        "Properties" text,
        "Scopes" text,
        "Status" character varying(50),
        "Subject" character varying(400),
        "Type" character varying(50),
        CONSTRAINT "PK_OpenIddictAuthorizations" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_OpenIddictAuthorizations_OpenIddictApplications_Application~" FOREIGN KEY ("ApplicationId") REFERENCES "OpenIddictApplications" ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261002203924_InitialOpenIddictSchema') THEN
    CREATE TABLE "OpenIddictTokens" (
        "Id" text NOT NULL,
        "ApplicationId" text,
        "AuthorizationId" text,
        "ConcurrencyToken" character varying(50),
        "CreationDate" timestamp with time zone,
        "ExpirationDate" timestamp with time zone,
        "Payload" text,
        "Properties" text,
        "RedemptionDate" timestamp with time zone,
        "ReferenceId" character varying(100),
        "Status" character varying(50),
        "Subject" character varying(400),
        "Type" character varying(150),
        CONSTRAINT "PK_OpenIddictTokens" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_OpenIddictTokens_OpenIddictApplications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES "OpenIddictApplications" ("Id"),
        CONSTRAINT "FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId" FOREIGN KEY ("AuthorizationId") REFERENCES "OpenIddictAuthorizations" ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261002203924_InitialOpenIddictSchema') THEN
    CREATE UNIQUE INDEX "IX_OpenIddictApplications_ClientId" ON "OpenIddictApplications" ("ClientId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261002203924_InitialOpenIddictSchema') THEN
    CREATE INDEX "IX_OpenIddictAuthorizations_ApplicationId_Status_Subject_Type" ON "OpenIddictAuthorizations" ("ApplicationId", "Status", "Subject", "Type");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261002203924_InitialOpenIddictSchema') THEN
    CREATE UNIQUE INDEX "IX_OpenIddictScopes_Name" ON "OpenIddictScopes" ("Name");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261002203924_InitialOpenIddictSchema') THEN
    CREATE INDEX "IX_OpenIddictTokens_ApplicationId_Status_Subject_Type" ON "OpenIddictTokens" ("ApplicationId", "Status", "Subject", "Type");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261002203924_InitialOpenIddictSchema') THEN
    CREATE INDEX "IX_OpenIddictTokens_AuthorizationId" ON "OpenIddictTokens" ("AuthorizationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261002203924_InitialOpenIddictSchema') THEN
    CREATE UNIQUE INDEX "IX_OpenIddictTokens_ReferenceId" ON "OpenIddictTokens" ("ReferenceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261002203924_InitialOpenIddictSchema') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261002203924_InitialOpenIddictSchema', '10.0.11');
    END IF;
END $EF$;
COMMIT;



-- 应用角色授权：与其它服务保持同一套权限边界。
-- simpleshop_app 在 public schema 上没有 CREATE 权限（表结构只由本目录的脚本建立），
-- 但需要对已建好的表有 DML 权限。
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO simpleshop_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO simpleshop_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO simpleshop_app;