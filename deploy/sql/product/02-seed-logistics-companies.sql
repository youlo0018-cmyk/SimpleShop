-- ============================================================================
-- 物流公司字典种子数据（DATA_SPEC 5.23「内置常用物流公司」）
--
-- 幂等写法说明：
--   用 ON CONFLICT **只对 partial unique 索引**有效的前提是冲突目标要写全谓词。
--   这里不用 ON CONFLICT，而是 WHERE NOT EXISTS —— 因为索引是 partial 的
--   （WHERE is_deleted = false），PostgreSQL 的 ON CONFLICT (company_name)
--   推不出该走哪个索引，会直接报 "no unique or exclusion constraint matching"。
--   重跑时若运营已把某个公司软删，NOT EXISTS 会重新插一条——
--   这正是想要的：软删不影响「内置列表」的存在性，只是那条不再出现在下拉里。
-- ============================================================================

INSERT INTO logistics_company
    (id, created_at, company_name, company_code, sort_order, status, remark)
SELECT 900001, now(), '顺丰速运',   'sf',   10, 1, '内置'
WHERE NOT EXISTS (SELECT 1 FROM logistics_company WHERE company_name = '顺丰速运' AND is_deleted = false);

INSERT INTO logistics_company
    (id, created_at, company_name, company_code, sort_order, status, remark)
SELECT 900002, now(), '中通快递',   'zto',  20, 1, '内置'
WHERE NOT EXISTS (SELECT 1 FROM logistics_company WHERE company_name = '中通快递' AND is_deleted = false);

INSERT INTO logistics_company
    (id, created_at, company_name, company_code, sort_order, status, remark)
SELECT 900003, now(), '圆通速递',   'yto',  30, 1, '内置'
WHERE NOT EXISTS (SELECT 1 FROM logistics_company WHERE company_name = '圆通速递' AND is_deleted = false);

INSERT INTO logistics_company
    (id, created_at, company_name, company_code, sort_order, status, remark)
SELECT 900004, now(), '韵达快递',   'yd',   40, 1, '内置'
WHERE NOT EXISTS (SELECT 1 FROM logistics_company WHERE company_name = '韵达快递' AND is_deleted = false);

INSERT INTO logistics_company
    (id, created_at, company_name, company_code, sort_order, status, remark)
SELECT 900005, now(), '申通快递',   'st',   50, 1, '内置'
WHERE NOT EXISTS (SELECT 1 FROM logistics_company WHERE company_name = '申通快递' AND is_deleted = false);

INSERT INTO logistics_company
    (id, created_at, company_name, company_code, sort_order, status, remark)
SELECT 900006, now(), '极兔速递',   'jtx',  60, 1, '内置'
WHERE NOT EXISTS (SELECT 1 FROM logistics_company WHERE company_name = '极兔速递' AND is_deleted = false);

INSERT INTO logistics_company
    (id, created_at, company_name, company_code, sort_order, status, remark)
SELECT 900007, now(), '京东物流',   'jd',   70, 1, '内置'
WHERE NOT EXISTS (SELECT 1 FROM logistics_company WHERE company_name = '京东物流' AND is_deleted = false);

INSERT INTO logistics_company
    (id, created_at, company_name, company_code, sort_order, status, remark)
SELECT 900008, now(), '邮政EMS',    'ems',  80, 1, '内置'
WHERE NOT EXISTS (SELECT 1 FROM logistics_company WHERE company_name = '邮政EMS' AND is_deleted = false);

INSERT INTO logistics_company
    (id, created_at, company_name, company_code, sort_order, status, remark)
SELECT 900009, now(), '德邦快递',   'db',   90, 1, '内置'
WHERE NOT EXISTS (SELECT 1 FROM logistics_company WHERE company_name = '德邦快递' AND is_deleted = false);

INSERT INTO logistics_company
    (id, created_at, company_name, company_code, sort_order, status, remark)
SELECT 900010, now(), '菜鸟裹裹',   'cainiao', 100, 1, '内置'
WHERE NOT EXISTS (SELECT 1 FROM logistics_company WHERE company_name = '菜鸟裹裹' AND is_deleted = false);
