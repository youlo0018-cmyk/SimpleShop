<#
+.SYNOPSIS
+    播种 6 个内置角色及其权限点绑定（BUSINESS.md 5.1）。
+.DESCRIPTION
+    平台管理员与商户管理员绑定**全部**权限点；其余角色按文档描述绑定子集。
+    内置角色 IsBuiltin = true：禁止编辑、禁止删除、权限锁定（DATA_SPEC 5.21）。
+    幂等：先删绑定再重建，角色按 code 覆盖更新。
+    用 COPY ... FROM STDIN 走 CSV，不拼 INSERT 字符串。
+#>
[CmdletBinding()]
param(
    [string]$Container = 'simpleshop-postgres',
    [string]$Database = 'simpleshoppermission',
    [string]$User = 'postgres'
)

$ErrorActionPreference = 'Stop'

# CSV 语义：未加引号的空字段是 NULL，加引号的空串是空字符串。
# timestamp 列不能填空字符串，所以空值必须保持未加引号。
function Q([string]$v) {
    if ([string]::IsNullOrEmpty($v)) { return '' }
    return '"' + $v.Replace('"', '""') + '"'
}

# 角色：Name / Code / AllowedScopes / DataScope / 权限点匹配前缀
# 前缀为空表示「全部权限点」
# Exclude：从匹配结果里**剔除**的权限点编码（逗号分隔）。
# 为什么需要它：「平台运营」的 module 前缀是 platform，而 platform 下同时有
# platform:read / platform:update / platform:create —— 用前缀一把捞会把
# **新建平台**也带上，那违反「平台由超级管理员添加」。
# 写成「前缀 + 排除」而不是「逐个列举权限点」：列举的话以后新增
# platform 下的其它权限点（比如平台导出）就默认拿不到，而它们本意是给运营的。
$roles = @(
    @{ Name = '平台管理员';   Code = 'platform-admin';     Scopes = 1; DataScope = 2; Prefix = '' },
    @{ Name = '平台运营';     Code = 'platform-operator';  Scopes = 1; DataScope = 2; Prefix = 'platform|merchant|category|product|inventory|order|refund|marketing|coupon|seckill|point|evaluate|design|report|dashboard|region|permission|user|customer'
       Exclude = 'platform:create' },
    @{ Name = '平台财务';     Code = 'platform-finance';   Scopes = 1; DataScope = 2; Prefix = 'order|payment|refund|report|dashboard' },
    @{ Name = '商户管理员';   Code = 'merchant-admin';     Scopes = 2; DataScope = 2; Prefix = '' },
    @{ Name = '商户运营';     Code = 'merchant-operator';  Scopes = 2; DataScope = 1; Prefix = 'product|inventory|order' },
    @{ Name = '商户财务';     Code = 'merchant-finance';   Scopes = 2; DataScope = 1; Prefix = 'order|refund|report' }
)

$now = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')

# 读出全部权限点（code, id）
$permRows = docker exec $Container psql -U $User -d $Database -tAc "select code || '|' || id from permission where level = 3 and is_deleted = false and code <> ''"
$allPerms = @()
foreach ($line in $permRows) {
    if (-not $line) { continue }
    $parts = $line.Trim().Split('|')
    if ($parts.Count -eq 2) { $allPerms += @{ Code = $parts[0]; Id = $parts[1] } }
}
Write-Host ("==> 读到权限点 " + $allPerms.Count + " 个")

$roleIdByCode = @{}
$roleId = 9001
foreach ($r in $roles) { $roleIdByCode[$r.Code] = $roleId; $roleId++ }

# 角色 CSV
$roleCsv = @('role_id,created_at,updated_at,is_deleted,deleted_at,role_name,role_code,allowed_scopes,data_scope,status,is_builtin,remark')
foreach ($r in $roles) {
    $roleCsv += ((@($roleIdByCode[$r.Code], $now, '', 'false', '', $r.Name, $r.Code, $r.Scopes, $r.DataScope, 1, 'true', '内置角色') |
        ForEach-Object { if ($_ -is [int]) { [string]$_ } else { Q $_ } }) -join ',')
}

# 绑定 CSV：role_id,permission_id,created_at
$bindCsv = @('role_id,permission_id,created_at')
foreach ($r in $roles) {
    $rid = $roleIdByCode[$r.Code]
    $targets = if ([string]::IsNullOrEmpty($r.Prefix)) {
        $allPerms
    } else {
        $rx = $r.Prefix -split '\|'
        $allPerms | Where-Object { $code = $_.Code; ($rx | Where-Object { $code.StartsWith($_) }).Count -gt 0 }
    }
    # 应用排除项。放在前缀筛选**之后**，语义是「先按模块捞，再从里面剔掉个别动作」。
    if (-not [string]::IsNullOrEmpty($r.Exclude)) {
        $ex = $r.Exclude -split ','
        $targets = $targets | Where-Object { $ex -notcontains $_.Code }
    }
    foreach ($p in $targets) { $bindCsv += "$rid,$($p.Id),$now" }
}

$roleIds = ($roles | ForEach-Object { $roleIdByCode[$_.Code] }) -join ','
$deleteSql = "DELETE FROM role_permission WHERE role_id IN ($roleIds);"
$deleteRole = "DELETE FROM role WHERE id IN ($roleIds);"

$sql = @(
    'BEGIN;'
    $deleteSql
    $deleteRole
    "COPY role (id, created_at, updated_at, is_deleted, deleted_at, role_name, role_code, allowed_scopes, data_scope, status, is_builtin, remark) FROM STDIN WITH (FORMAT csv, HEADER true);"
    ($roleCsv -join "`n")
    '\.'
    "COPY role_permission (role_id, permission_id, created_at) FROM STDIN WITH (FORMAT csv, HEADER true);"
    ($bindCsv -join "`n")
    '\.'
    'COMMIT;'
) -join "`n"

$out = $sql | docker exec -i $Container psql -U $User -d $Database -v ON_ERROR_STOP=1 2>&1
if ($LASTEXITCODE -ne 0) {
    $out | Select-Object -First 4 | ForEach-Object { Write-Host $_ }
    throw "内置角色播种失败，退出码 $LASTEXITCODE"
}

$count = (docker exec $Container psql -U $User -d $Database -tAc "select count(*) from role where is_deleted = false").Trim()
$bind = (docker exec $Container psql -U $User -d $Database -tAc "select count(*) from role_permission").Trim()
Write-Host "==> 完成：内置角色 $count 个，权限绑定 $bind 条" -ForegroundColor Green
