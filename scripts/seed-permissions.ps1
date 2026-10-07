<#
.SYNOPSIS
    播种权限树：5 个业务大类 + 23 个功能模块 + 77 个权限点。
.DESCRIPTION
    依据 BUSINESS.md 5.2 的权限点清单与 5.4 的 4 层树结构。
    幂等：已存在的 code 不重复插入，先删后插按 code 对齐。
    ID 使用确定性编号（结构节点 2001/2101 段，叶子 3001 段）便于排障与关联。
    api_path 支持逗号分隔的多条路径（BUSINESS.md 5.4「ApiPath 可多个」）。
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

# ---- 第 3 层：权限点（79 个），Key 为模块 Id ----
$leaves = [ordered]@{
    '2101' = @(@('user:read', '账号列表', '/gateway/users/List'), @('user:create', '新建账号', '/gateway/users/Create'),
             @('user:update', '编辑账号与重置密码', '/gateway/users/Update,/gateway/users/ResetPassword'), @('user:status', '启停账号', '/gateway/users/UpdateStatus'))
    # 一个权限点**只能映射一条路径**（permission.code 上有唯一约束）。
    # 同一个权限点要覆盖多个接口时用 /* 通配，而不是把 code 写两遍——
    # 写成两遍会撞 uk_permission_code，整个播种直接失败。
    '2102' = @(@('customer:read', '客户列表与详情', '/gateway/admin/customers/*'),
             @('customer:status', '启停客户', '/gateway/admin/customers/ChangeStatus'))
    # 角色 CRUD 在**另一个控制器**上（RoleController，路由是 roles/），
    # 权限树 CRUD 在 PermissionController（路由是 permissions/）。
    # 之前四个角色权限点全绑成了 /gateway/permissions/Roles*，
    # 那里根本没有 Roles 子路径 —— 网关查不到映射就**放行**，
    # 等于「角色增删改」四个接口完全不鉴权（scripts/check-permission-paths.ps1 抓出来的）。
    '2103' = @(@('permission:read', '角色列表', '/gateway/roles/List,/gateway/roles/Detail,/gateway/roles/Options'), @('permission:create', '新建角色', '/gateway/roles/Create'),
             @('permission:update', '编辑角色', '/gateway/roles/Update'), @('permission:delete', '删除角色', '/gateway/roles/Delete'),
             @('permission:manage', '权限点管理与角色授权', '/gateway/permissions/*,/gateway/roles/BindPermissions'))
    # platform:audit 之前绑 /gateway/platforms/Audit，而该端点**根本不存在**
    # （平台只有 List/Create/Update/Delete/Options）。
    # 商户审核是真实流程（merchants/Audit），平台审核在规格里没有对应流程，
    # 所以这个叶子节点本就不该存在——按 BUSINESS.md 5.2 的清单删除它，
    # 权限点总数随之从 78 变为 77（见 BUSINESS.md 同步说明）。
    # platforms/Options 是后台多个表单的「所属平台」下拉数据源。
    # 漏了它，后台账号在 fail-closed 之后连下拉都拉不出来（403），
    # 而报错只会显示「请求失败」，看不出是权限种子少了一条。
    '2104' = @(@('platform:read', '平台列表', '/gateway/platforms/List,/gateway/platforms/Options'), @('platform:create', '新建平台', '/gateway/platforms/Create'),
             @('platform:update', '编辑与删除平台', '/gateway/platforms/Update,/gateway/platforms/Delete'))
    '2105' = @(@('merchant:read', '商户列表', '/gateway/merchants/List,/gateway/merchants/Options'), @('merchant:create', '新建商户', '/gateway/merchants/Create'),
             @('merchant:update', '编辑 / 删除 / 重新提交 / 启停商户', '/gateway/merchants/Update,/gateway/merchants/Delete,/gateway/merchants/Resubmit,/gateway/merchants/ChangeStatus'), @('merchant:audit', '商户审核', '/gateway/merchants/Audit'))
    # 地区在 MerchantPlatformService 的独立控制器上，路由是 regions/，
    # **不存在** platform-configs 这个前缀。
    '2106' = @(@('region:read', '地区地址查看', '/gateway/regions/Get'), @('region:update', '地区地址维护', '/gateway/regions/Save'))
    # 分类只有一个树形接口，没有单独的 List。
    '2107' = @(@('category:read', '分类列表', '/gateway/categories/Tree'), @('category:create', '新建分类', '/gateway/categories/Create'),
             @('category:update', '编辑分类', '/gateway/categories/Update'), @('category:delete', '删除分类', '/gateway/categories/Delete'))
    # DATA_SPEC 5.6 明确要求 Create 与 Save **两个端点**：
    # Create 只建（传了 ProductId 直接拒绝），Save 只改（Id 必须存在）。
    # 之前只有 Save 一个端点，于是 product:create 无处可绑——它绑的
    # /gateway/products/Create 查不到映射，**新建商品接口等于不鉴权**。
    # 拆成两个端点还有一个好处：「能改价」与「能建档」变成两种可分别授予的能力。
    # products/Detail 是后台商品编辑页的详情接口，brands/* 按 DATA_SPEC 5.21 复用 product:*。
    # 这两组此前都没绑，而「查不到映射 = 放行」—— 后台详情与品牌增删改一直是不鉴权的。
    '2109' = @(@('product:read', '商品列表', '/gateway/products/List,/gateway/products/Detail,/gateway/products/Options,/gateway/products/Designable,/gateway/products/Skus,/gateway/brands/List,/gateway/brands/Options'), @('product:create', '新建商品', '/gateway/products/Create,/gateway/brands/Create'),
             @('product:update', '编辑 / 提交审核 / 上下架', '/gateway/products/Save,/gateway/products/SubmitAudit,/gateway/products/ChangeListing,/gateway/brands/Update'), @('product:audit', '商品审核', '/gateway/products/Audit'),
             @('product:delete', '删除商品', '/gateway/products/Delete,/gateway/brands/Delete'))
    # inventory/Flows 是库存流水（后台「库存」页的明细），同样漏绑。
    '2110' = @(@('inventory:read', '库存查询', '/gateway/inventory/List,/gateway/inventory/Flows'), @('inventory:update', '库存调整', '/gateway/inventory/Adjust'))
    # 后台订单一律走 /gateway/admin/orders/*，C 端订单走 /gateway/orders/*。
    # 分成两个前缀是刻意的：后台的「发货 / 退款 / 取货核销 / 模拟支付」权限点不能被小程序命中。
    # 🔴 Detail / Refunds 必须显式列出来。之前只绑了 List，而网关的判定是
    # 「api_path 匹配不到 → requiredCode 为 null → **放行**」，所以订单详情
    # 一直处于「谁登录都能看」的状态 —— 包括别的商户的订单。
    '2111' = @(@('order:read', '订单列表与详情', '/gateway/admin/orders/List,/gateway/admin/orders/Detail,/gateway/admin/orders/Refunds'), @('order:ship', '订单发货', '/gateway/admin/orders/Ship'),
             @('order:virtual-deliver', '虚拟发货', '/gateway/admin/orders/DeliverVirtual'),
             @('order:pickup', '取货核销', '/gateway/admin/orders/VerifyPickupCode'),
             @('order:pickup-ready', '备货完成', '/gateway/admin/orders/SelfPickupReady'),
             # 🔴 payments/Simulate 是**同一个动作的第二条路径**（规格 5.28 记的后台入口），
             # 它落在 PaymentService 的 PaymentController 上，与 /gateway/admin/orders/SimulatePayment
             # 是两套端点。只绑了后者，前者就一直查不到映射 —— 而「查不到 = 放行」，
             # 于是**顾客拿自己的客户令牌就能把自己的订单标成已支付**，白拿商品。
             # 两条路径必须绑同一个权限点，否则补了一条另一条还是洞。
             @('order:simulate', '模拟支付', '/gateway/admin/orders/SimulatePayment,/gateway/payments/Simulate'),
             @('order:refund', '订单退款与取消', '/gateway/admin/orders/Refund,/gateway/admin/orders/Cancel'),
             # 物流公司字典（DATA_SPEC 5.23）落在 ProductService：它是发货表单的下拉数据源。
             # 之前绑的是不存在的 /gateway/logistics/*，同样等于不鉴权。
             @('logistics:manage', '物流公司维护', '/gateway/logistics-companies/*'))
    # 支付单：后台走 /gateway/admin/payments/List（真实端点在 AdminPaymentController 上）。
    # 这里必须和真实端点一字不差 —— 网关按 api_path 最长匹配，匹配不到就是**放行**。
    '2112' = ,@(@('payment:read', '支付单列表', '/gateway/admin/payments/List'))
    # 退款单详情与审批同在 /gateway/refunds/* 下，用 /* 通配一次覆盖，省得再加叶子。
    # 退款「发起」在 refunds/Apply 上，不在 payments/ 下（那边是支付单）。
    # refunds/Detail 是退款单详情（含金额与审批记录），漏绑时任何登录用户都能读到别家的退款数据。
    '2113' = @(@('refund:read', '退款单列表与详情', '/gateway/refunds/List,/gateway/refunds/Detail'), @('refund:apply', '发起退款', '/gateway/refunds/Apply'),
             @('refund:approve', '审批退款', '/gateway/refunds/Approve'), @('refund:reject', '拒绝退款', '/gateway/refunds/Reject'))
    # activities/Get 是活动详情（编辑页要回填），漏绑时任何登录用户都能读活动配置。
    '2114' = @(@('marketing:read', '活动列表', '/gateway/marketing/activities/List,/gateway/marketing/activities/Get'), @('marketing:create', '新建活动', '/gateway/marketing/activities/Create'),
             @('marketing:update', '编辑 / 启停活动', '/gateway/marketing/activities/Update,/gateway/marketing/activities/SetStatus'), @('marketing:delete', '删除活动', '/gateway/marketing/activities/Delete'))
    # 券模板 / 券活动之前只绑了 List 与 Create，但真实端点当时只有 Get 与 Create，
    # 于是 List / Update / Delete 全都查不到映射 —— 不鉴权。端点已补齐，路径对齐。
    '2115' = @(@('coupon-template:read', '券模板列表', '/gateway/marketing/coupon-templates/List,/gateway/marketing/coupon-templates/Get,/gateway/marketing/coupon-templates/Options'), @('coupon-template:create', '新建券模板', '/gateway/marketing/coupon-templates/Create'),
             @('coupon-template:update', '编辑券模板', '/gateway/marketing/coupon-templates/Update'), @('coupon-template:delete', '删除券模板', '/gateway/marketing/coupon-templates/Delete'),
             @('coupon-activity:read', '券活动列表', '/gateway/marketing/coupon-activities/List,/gateway/marketing/coupon-activities/Get'), @('coupon-activity:create', '新建券活动', '/gateway/marketing/coupon-activities/Create'),
             @('coupon-activity:update', '编辑券活动', '/gateway/marketing/coupon-activities/Update,/gateway/marketing/coupon-activities/SetStatus'), @('coupon-record:read', '券核销记录', '/gateway/marketing/coupon-records/List'))
    # 营销配置在 marketing 命名空间下：/gateway/marketing/marketing-config/{Get,Save}。
    '2116' = @(@('marketing-config:read', '营销配置查看', '/gateway/marketing/marketing-config/Get'), @('marketing-config:update', '营销配置维护', '/gateway/marketing/marketing-config/Save'))
    # 秒杀场次是 **seckill/sessions/**（两段），不是 seckill-sessions（一段）。
    # 结束动作的真名是 Finish，不是 End。
    '2117' = @(@('seckill:read', '秒杀场次列表', '/gateway/marketing/seckill/sessions/List,/gateway/marketing/seckill/sessions/Options'), @('seckill:create', '新建场次', '/gateway/marketing/seckill/sessions/Create'),
             @('seckill:update', '编辑 / 发布 / 场次商品', '/gateway/marketing/seckill/sessions/Update,/gateway/marketing/seckill/sessions/Publish,/gateway/marketing/seckill/sessions/Items/*'), @('seckill:end', '结束中止场次', '/gateway/marketing/seckill/sessions/Finish'))
    # 后台积分流水走 points/RecordsAll（跨客户），C 端的 points/Records 是「只看自己的」，
    # 刻意不绑权限点：它是客户令牌访问的，带上后台权限点会把小程序自己的积分页挡掉。
    # 积分报表在 reports/Point 上（report:view 的通配已覆盖），规则维护在 points/Rules。
    # points/SaveRules 是**真正写入**积分规则的端点，points/Rules 只是读当前规则。
    # 只绑了读的那条，写的那条一直不鉴权 —— 任何登录用户都能把「每消费 1 元得 1 分」
    # 改成「得 100 分」，然后正常下单把积分刷出来。
    '2118' = @(@('point:read', '积分流水', '/gateway/points/RecordsAll'), @('point:rule-update', '积分规则维护', '/gateway/points/Rules,/gateway/points/SaveRules'))
    # ⚠️ 后台评价端点在 EvaluateAdminController 上，路由是 **evaluates/admin/**
    # （真实路径 /gateway/evaluates/admin/Reply 等）。
    # 这里原本绑的是 /gateway/evaluates/Reply —— 少了一层 admin，
    # 结果网关匹配不上、requiredCode 为 null，而未映射的路径是**放行**的，
    # 等于三个后台评价接口（列表 / 隐藏 / 回复）**完全没有鉴权**。
    '2119' = @(@('evaluate:read', '评价列表', '/gateway/evaluates/admin/List'), @('evaluate:manage', '隐藏评价', '/gateway/evaluates/admin/Hide'),
             @('evaluate:reply', '评价回复', '/gateway/evaluates/admin/Reply'))
    # 装修在 MerchantPlatformService 的 design/ 下。
    # 七个端点里平台装修与商户装修**交错排列**（Platform / Merchant / Components /
    # SavePlatformDraft / SaveMerchantDraft / PublishPlatform / PublishMerchant），
    # 没有任何一个公共前缀能把它们干净地分开——所以只能一个权限点绑多条路径。
    # 这正是 BUSINESS.md 5.4「ApiPath 可多个」的用武之地，逗号分隔。
    '2120' = @(@('design:read', '平台装修查看', '/gateway/design/Platform,/gateway/design/Components'),
             @('design:update', '平台装修维护', '/gateway/design/SavePlatformDraft,/gateway/design/PublishPlatform'),
             @('design:merchant', '商户店铺装修', '/gateway/design/Merchant,/gateway/design/SaveMerchantDraft,/gateway/design/PublishMerchant'))
    '2121' = @(@('dashboard:view', '工作台看板', '/gateway/reports/Report'), @('report:view', '经营报表', '/gateway/reports/*'),
             # 营销效果报表的**下钻**（按活动翻参与订单）也算这张报表的能力，
             # 绑在同一个权限点上：只给「看报表」不给「看明细」没有实际意义。
             @('report:marketing', '营销效果报表', '/gateway/reports/Marketing,/gateway/marketing/activities/Records'),
             @('report:seckill', '秒杀效果报表', '/gateway/reports/Seckill'))
    # 重建索引与索引对账都在 ProductService（端点本轮新增）。
    '2122' = ,@(@('search:reindex', '重建商品索引与对账', '/gateway/products/SearchIndex/*'))
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
