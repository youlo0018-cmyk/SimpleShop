<#
.SYNOPSIS
    播种权限树：5 个业务大类 + 23 个功能模块 + 78 个权限点。
.DESCRIPTION
    依据 BUSINESS.md 5.2 的权限点清单与 5.4 的 4 层树结构。
    幂等：已存在的 code 不重复插入，先删后插按 code 对齐。
    ID 使用确定性编号（结构节点 2001/2101 段，叶子 3001 段）便于排障与关联。
#>
[CmdletBinding()]
param(
    [string]$Container = 'simpleshop-postgres',
    [string]$Database = 'simpleshoppermission',
    [string]$User = 'postgres'
)

$ErrorActionPreference = 'Stop'

# ---- 第 1 层：业务大类（5 个）----
$groups = @(
    @{ Id = 2001; Name = '系统管理'; Sort = 1 }
    @{ Id = 2002; Name = '商品中心'; Sort = 2 }
    @{ Id = 2003; Name = '交易管理'; Sort = 3 }
    @{ Id = 2004; Name = '营销中心'; Sort = 4 }
    @{ Id = 2005; Name = '数据报表'; Sort = 5 }
)

# ---- 第 2 层：功能模块（23 个），Key 为大类 Id，Value 为 @{Id;Name} ----
$modules = [ordered]@{
    '2001' = @(@{ Id = 2101; Name = '账号' }, @{ Id = 2102; Name = '客户' }, @{ Id = 2103; Name = '角色权限' },
             @{ Id = 2104; Name = '平台' }, @{ Id = 2105; Name = '商户' }, @{ Id = 2106; Name = '地区地址' },
             @{ Id = 2107; Name = '分类' })
    '2002' = @(@{ Id = 2108; Name = '品牌' }, @{ Id = 2109; Name = '商品' }, @{ Id = 2110; Name = '库存' })
    '2003' = @(@{ Id = 2111; Name = '订单' }, @{ Id = 2112; Name = '支付' }, @{ Id = 2113; Name = '退款' })
    '2004' = @(@{ Id = 2114; Name = '营销活动' }, @{ Id = 2115; Name = '券' }, @{ Id = 2116; Name = '营销配置' },
             @{ Id = 2117; Name = '限时抢购' }, @{ Id = 2118; Name = '积分' }, @{ Id = 2119; Name = '评价' })
    '2005' = @(@{ Id = 2120; Name = '装修' }, @{ Id = 2121; Name = '报表' }, @{ Id = 2122; Name = '搜索索引' },
             @{ Id = 2123; Name = '文件与日志' })
}

# ---- 第 3 层：权限点（78 个），Key 为模块 Id ----
$leaves = [ordered]@{
    '2101' = @(@('user:read', '账号列表', '/gateway/users/List'), @('user:create', '新建账号', '/gateway/users/Create'),
             @('user:update', '编辑账号', '/gateway/users/Update'), @('user:status', '启停账号', '/gateway/users/UpdateStatus'))
    '2102' = @(@('customer:read', '客户列表', '/gateway/customers/Admin/List'), @('customer:update', '编辑客户', '/gateway/customers/Admin/Update'),
             @('customer:status', '启停客户', '/gateway/customers/Admin/UpdateStatus'))
    '2103' = @(@('permission:read', '角色列表', '/gateway/permissions/Roles'), @('permission:create', '新建角色', '/gateway/permissions/Roles/Create'),
             @('permission:update', '编辑角色', '/gateway/permissions/Roles/Update'), @('permission:delete', '删除角色', '/gateway/permissions/Roles/Delete'),
             @('permission:manage', '权限点管理', '/gateway/permissions/*'))
    '2104' = @(@('platform:read', '平台列表', '/gateway/platforms/List'), @('platform:create', '新建平台', '/gateway/platforms/Create'),
             @('platform:update', '编辑平台', '/gateway/platforms/Update'), @('platform:audit', '平台审核', '/gateway/platforms/Audit'))
    '2105' = @(@('merchant:read', '商户列表', '/gateway/merchants/List'), @('merchant:create', '新建商户', '/gateway/merchants/Create'),
             @('merchant:update', '编辑商户', '/gateway/merchants/Update'), @('merchant:audit', '商户审核', '/gateway/merchants/Audit'))
    '2106' = @(@('region:read', '地区地址查看', '/gateway/platform-configs/Regions'), @('region:update', '地区地址维护', '/gateway/platform-configs/SaveRegions'))
    '2107' = @(@('category:read', '分类列表', '/gateway/categories/List'), @('category:create', '新建分类', '/gateway/categories/Create'),
             @('category:update', '编辑分类', '/gateway/categories/Update'), @('category:delete', '删除分类', '/gateway/categories/Delete'))
    '2109' = @(@('product:read', '商品列表', '/gateway/products/List'), @('product:create', '新建商品', '/gateway/products/Create'),
             @('product:update', '编辑商品', '/gateway/products/Save'), @('product:audit', '商品审核', '/gateway/products/Audit'),
             @('product:delete', '删除商品', '/gateway/products/Delete'))
    '2110' = @(@('inventory:read', '库存查询', '/gateway/inventory/List'), @('inventory:update', '库存调整', '/gateway/inventory/Adjust'))
    '2111' = @(@('order:read', '订单列表', '/gateway/orders/List'), @('order:ship', '订单发货', '/gateway/orders/Ship'),
             @('order:receive', '确认收货', '/gateway/orders/Receive'), @('order:cancel', '取消订单', '/gateway/orders/Cancel'),
             @('order:pickup', '取货核销', '/gateway/orders/Pickup'), @('order:simulate', '模拟支付', '/gateway/payments/Simulate'),
             @('logistics:manage', '物流公司维护', '/gateway/logistics/*'))
    '2112' = ,@(@('payment:read', '支付单列表', '/gateway/payments/List'))
    '2113' = @(@('refund:read', '退款单列表', '/gateway/refunds/List'), @('refund:apply', '发起退款', '/gateway/payments/Refund'),
             @('refund:approve', '审批退款', '/gateway/refunds/Approve'), @('refund:reject', '拒绝退款', '/gateway/refunds/Reject'))
    '2114' = @(@('marketing:read', '活动列表', '/gateway/marketing/activities/List'), @('marketing:create', '新建活动', '/gateway/marketing/activities/Create'),
             @('marketing:update', '编辑活动', '/gateway/marketing/activities/Update'), @('marketing:delete', '删除活动', '/gateway/marketing/activities/Delete'))
    '2115' = @(@('coupon-template:read', '券模板列表', '/gateway/marketing/coupon-templates/List'), @('coupon-template:create', '新建券模板', '/gateway/marketing/coupon-templates/Create'),
             @('coupon-template:update', '编辑券模板', '/gateway/marketing/coupon-templates/Update'), @('coupon-template:delete', '删除券模板', '/gateway/marketing/coupon-templates/Delete'),
             @('coupon-activity:read', '券活动列表', '/gateway/marketing/coupon-activities/List'), @('coupon-activity:create', '新建券活动', '/gateway/marketing/coupon-activities/Create'),
             @('coupon-activity:update', '编辑券活动', '/gateway/marketing/coupon-activities/Update'), @('coupon-record:read', '券核销记录', '/gateway/marketing/coupon-records/List'))
    '2116' = @(@('marketing-config:read', '营销配置查看', '/gateway/marketing-config/Get'), @('marketing-config:update', '营销配置维护', '/gateway/marketing-config/Save'))
    '2117' = @(@('seckill:read', '秒杀场次列表', '/gateway/marketing/seckill-sessions/List'), @('seckill:create', '新建场次', '/gateway/marketing/seckill-sessions/Create'),
             @('seckill:update', '编辑场次', '/gateway/marketing/seckill-sessions/Update'), @('seckill:end', '结束中止场次', '/gateway/marketing/seckill-sessions/End'))
    '2118' = @(@('point:read', '积分报表', '/gateway/points/Report'), @('point:rule-update', '积分规则维护', '/gateway/points/Rules'))
    '2119' = @(@('evaluate:read', '评价列表', '/gateway/evaluates/List'), @('evaluate:manage', '评价管理', '/gateway/evaluates/Manage'),
             @('evaluate:reply', '评价回复', '/gateway/evaluates/Reply'))
    '2120' = @(@('design:read', '装修查看', '/gateway/platform-configs/Design'), @('design:update', '装修维护', '/gateway/platform-configs/SaveDraft'),
             @('design:merchant', '商户装修', '/gateway/merchant-configs/*'))
    '2121' = @(@('dashboard:view', '工作台看板', '/gateway/reports/Report'), @('report:view', '经营报表', '/gateway/reports/*'),
             @('report:marketing', '营销效果报表', '/gateway/reports/Marketing'), @('report:seckill', '秒杀效果报表', '/gateway/reports/Seckill'))
    '2122' = ,@(@('search:reindex', '重建商品索引', '/gateway/products/Reindex'))
    '2123' = @(@('file:upload', '文件上传', '/gateway/files/*'), @('log:read', '日志查询', '/gateway/logs/*'))
}

# 品牌模块（2108）没有独立权限点：按 DATA_SPEC 5.21 品牌复用 product:*，
# 所以它在树上表现为空节点，勾选时不产生叶子记录。

function Q([string]$v) { return "'" + $v.Replace("'", "''") + "'" }

$records = [System.Collections.Generic.List[object]]::new()

function Add-Row($vals)
{
    # 列顺序：id, created_at, name, code, api_path, parent_id, level, sort_order, status, is_builtin, description
    $records.Add([pscustomobject]@{
        id         = $vals[0]
        created_at = $vals[1]
        name       = $vals[2]
        code       = $vals[3]
        api_path   = $vals[4]
        parent_id  = $vals[5]
        level      = $vals[6]
        sort_order = $vals[7]
        status     = $vals[8]
        is_builtin = $vals[9]
        description = $vals[10]
    })
}

$now = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')

foreach ($g in $groups)
{
    Add-Row @($g.Id, $now, $g.Name, '', '', 0, 1, $g.Sort, 1, $true, '内置业务大类')
}

foreach ($groupId in $modules.Keys)
{
    $sort = 0
    foreach ($m in $modules[$groupId])
    {
        $sort++
        Add-Row @($m.Id, $now, $m.Name, '', '', $groupId, 2, $sort, 1, $true, '内置功能模块')
    }
}

$leafId = 3001
foreach ($moduleId in $leaves.Keys)
{
    $sort = 0
    foreach ($leaf in $leaves[$moduleId])
    {
        $sort++
        Add-Row @($leafId, $now, $leaf[1], $leaf[0], $leaf[2], $moduleId, 3, $sort, 1, $true, $leaf[2])
        $leafId++
    }
}

$leafCount = $leafId - 3001
$moduleCount = ($modules.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
Write-Host ("==> 权限树：{0} 大类 / {1} 模块 / {2} 权限点" -f $groups.Count, $moduleCount, $leafCount) -ForegroundColor Cyan

# 用 COPY ... FROM STDIN 走 CSV，而不是拼 INSERT 字符串。
# 拼字符串时要自己处理引号与列顺序，漏一处就是 syntax error；COPY 由 PostgreSQL 解析，
# 转义交给 ConvertTo-Csv，彻底避开这类坑。
$csv = ($records | ConvertTo-Csv -NoTypeInformation) -join "`n"
$structIds = (@($groups.Id) + @($modules.Values | ForEach-Object { $_.Id })) -join ','
$columns = 'id, created_at, name, code, api_path, parent_id, level, sort_order, status, is_builtin, description'

$sql = @(
    'BEGIN;'
    "DELETE FROM permission WHERE id IN ($structIds) OR code <> '';"
    "COPY permission ($columns) FROM STDIN WITH (FORMAT csv, HEADER true);"
    $csv
    '\.'
    'COMMIT;'
) -join "`n"

$out = $sql | docker exec -i $Container psql -U $User -d $Database -v ON_ERROR_STOP=1 2>&1
if ($LASTEXITCODE -ne 0)
{
    $out | Select-Object -First 4 | ForEach-Object { Write-Host $_ }
    throw "权限树播种失败，退出码 $LASTEXITCODE"
}

$count = (docker exec $Container psql -U $User -d $Database -tAc "select count(*) from permission where is_deleted = false").Trim()
$leafRows = (docker exec $Container psql -U $User -d $Database -tAc "select count(*) from permission where level = 3 and is_deleted = false").Trim()
Write-Host "==> 完成：库中权限节点 $count 条，其中叶子权限点 $leafRows 条" -ForegroundColor Green
