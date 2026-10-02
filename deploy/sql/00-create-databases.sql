-- SimpleShop 数据库初始化（幂等，可重复执行）
-- 依据：BUSINESS.md 3.3「每个服务一个独立数据库」、DATA_SPEC.md 2.9「不使用 CodeFirst」
-- 14 个库，每个服务一个，连接串只出现在配置中心（DATA_SPEC 1.1）
--
-- 实现说明：PostgreSQL 不允许在函数或 DO 块里执行 CREATE DATABASE / ALTER DATABASE，
-- 所以这里用 psql 的 gexec 做「查一次、拼一条、批量执行」，达到幂等效果。
-- 本脚本必须用 psql 执行，不能用只支持纯 SQL 的客户端。

-- 1) 应用角色（所有服务共用，开发环境不按服务细分 owner）
SELECT 'CREATE ROLE simpleshop_app LOGIN PASSWORD ''simpleshop_dev_2026'''
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'simpleshop_app')
\gexec

-- 2) 14 个服务库
SELECT format('CREATE DATABASE %I OWNER postgres ENCODING ''UTF8''', db_name)
FROM (VALUES
    ('simpleshopauth'),      ('simpleshopuser'),      ('simpleshopcustomer'),
    ('simpleshoptool'),      ('simpleshoppermission'),('simpleshopproduct'),
    ('simpleshopcart'),      ('simpleshopinventory'), ('simpleshoporder'),
    ('simpleshoppayment'),   ('simpleshopmarketing'), ('simpleshopmerchant'),
    ('simpleshoppoint'),     ('simpleshopevaluate')
) AS t(db_name)
WHERE NOT EXISTS (SELECT 1 FROM pg_database d WHERE d.datname = t.db_name)
\gexec

-- 3) 授权：应用角色对 14 个库有全部权限（表结构由各服务自己的 DDL 脚本管理）
SELECT format('GRANT ALL PRIVILEGES ON DATABASE %I TO simpleshop_app', db_name)
FROM (VALUES
    ('simpleshopauth'),      ('simpleshopuser'),      ('simpleshopcustomer'),
    ('simpleshoptool'),      ('simpleshoppermission'),('simpleshopproduct'),
    ('simpleshopcart'),      ('simpleshopinventory'), ('simpleshoporder'),
    ('simpleshoppayment'),   ('simpleshopmarketing'), ('simpleshopmerchant'),
    ('simpleshoppoint'),     ('simpleshopevaluate')
) AS t(db_name)
WHERE EXISTS (SELECT 1 FROM pg_database d WHERE d.datname = t.db_name)
\gexec

-- 4) 每个库统一使用 UTC 存储时间，展示层再转 Asia/Shanghai（DATA_SPEC 2.8）
SELECT format('ALTER DATABASE %I SET timezone TO ''UTC''', db_name)
FROM (VALUES
    ('simpleshopauth'),      ('simpleshopuser'),      ('simpleshopcustomer'),
    ('simpleshoptool'),      ('simpleshoppermission'),('simpleshopproduct'),
    ('simpleshopcart'),      ('simpleshopinventory'), ('simpleshoporder'),
    ('simpleshoppayment'),   ('simpleshopmarketing'), ('simpleshopmerchant'),
    ('simpleshoppoint'),     ('simpleshopevaluate')
) AS t(db_name)
WHERE EXISTS (SELECT 1 FROM pg_database d WHERE d.datname = t.db_name)
\gexec

-- 5) 结果校验：应为 14 行
SELECT datname FROM pg_database WHERE datname LIKE 'simpleshop%' ORDER BY datname;
