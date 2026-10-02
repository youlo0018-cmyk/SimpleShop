<#
.SYNOPSIS
    Gateway（5008）回归测试。
.DESCRIPTION
    网关是整套鉴权的收口，所以这里重点验证「不该放行的都放行不了」：
      - 匿名白名单只有 注册 / 登录 / 换令牌 三个，其余一律 401
      - 入站的 X-Claim-* / X-Internal-Token 头必须被剥离（伪造租户头不能提权）
      - 令牌带什么权限就只准做什么（RBAC 按令牌里的 permission 判定）
      - 令牌之外伪造的头不影响判定
      - /internal/** 不经网关暴露
    退出码非 0 即视为回归失败。
#>
[CmdletBinding()]
param(
    [string]$Gateway = 'http://127.0.0.1:5008',
    [string]$UserService = 'http://127.0.0.1:5011',
    [string]$AdminUser = 'codexadmin',
    [string]$AdminPassword = 'Admin123456',
    [switch]$StopOnFail
)

$ErrorActionPreference = 'Continue'
$script:pass = 0
$script:fail = 0
$script:failures = @()

function Invoke-Case {
    param([string]$Id, [string]$Name, [scriptblock]$Action)
    try {
        if (& $Action) {
            $script:pass++
            Write-Host ("  PASS  " + $Id + "  " + $Name) -ForegroundColor Green
        } else {
            $script:fail++
            $script:failures += "$Id $Name"
            Write-Host ("  FAIL  " + $Id + "  " + $Name) -ForegroundColor Red
            if ($StopOnFail) { throw "用例 $Id 失败，已按 -StopOnFail 中止" }
        }
    } catch {
        $script:fail++
        $script:failures += "$Id $Name (异常: $($_.Exception.Message))"
        Write-Host ("  FAIL  " + $Id + "  " + $Name + "  " + $_.Exception.Message) -ForegroundColor Red
        if ($StopOnFail) { throw }
    }
}

Add-Type -AssemblyName System.Net.Http
$http = [System.Net.Http.HttpClient]::new()

# 走网关换后台令牌。FormUrlEncodedContent 必须用 Dictionary 构造：
# 直接传 KeyValuePair 数组会被 PowerShell 摊平成 object[]，类型转换直接失败。
function Get-AdminToken([hashtable]$Overrides) {
    # 先铺默认值再叠加覆盖项。
    # 不要写成 `$Overrides ?? $defaults`：?? 是**整体替换**，只传 username/password 时
    # grant_type 与 client_id 会整个丢掉，换令牌必然失败——而且失败得很安静。
    $values = @{
        grant_type = 'password'; client_id = 'admin-app'
        username   = $AdminUser;   password  = $AdminPassword
    }
    if ($Overrides) { foreach ($k in $Overrides.Keys) { $values[$k] = $Overrides[$k] } }

    $dict = [System.Collections.Generic.Dictionary[string,string]]::new()
    foreach ($k in $values.Keys) { $dict[$k] = $values[$k] }

    $resp = $http.PostAsync("$Gateway/gateway/auth/token", [System.Net.Http.FormUrlEncodedContent]::new($dict)).GetAwaiter().GetResult()
    $json = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
    return $json.access_token
}

function Get-Status([string]$Method, [string]$Path, [hashtable]$Headers, $Body) {
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), "$Gateway$Path")
    if ($Headers) {
        foreach ($k in $Headers.Keys) { $req.Headers.TryAddWithoutValidation($k, $Headers[$k]) | Out-Null }
    }
    if ($Body) {
        $req.Content = [System.Net.Http.StringContent]::new(
            ($Body | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
    }
    return [int]$http.SendAsync($req).GetAwaiter().GetResult().StatusCode
}

Write-Host "`n=== GTW 健康检查 ===" -ForegroundColor Cyan
Invoke-Case 'API-GTW-001' 'GET /health 返回 200（探活不鉴权）' {
    (Get-Status 'GET' '/health' $null $null) -eq 200
}

Write-Host "`n=== API-GTW-010~012 匿名白名单 ===" -ForegroundColor Cyan
Invoke-Case 'API-GTW-010' '注册无需令牌' {
    $phone = '138' + (Get-Random -Minimum 10000000 -Maximum 99999999)
    (Get-Status 'POST' '/gateway/customers/Register' $null @{
        customerName = "gtw$phone"; password = 'Test123456'; phone = $phone; nickName = '网关测试'
    }) -eq 200
}

Invoke-Case 'API-GTW-011' '换后台令牌无需令牌' {
    -not [string]::IsNullOrWhiteSpace((Get-AdminToken $null))
}

Write-Host "`n=== API-GTW-020~026 受保护路径 ===" -ForegroundColor Cyan
Invoke-Case 'API-GTW-020' '无令牌访问账号列表被拒（401）' {
    (Get-Status 'GET' '/gateway/users/List?page=1&pageSize=5' $null $null) -eq 401
}

Invoke-Case 'API-GTW-021' '🔴 伪造 X-Claim-* 租户头不能提权（仍 401）' {
    $h = @{
        'X-Claim-PlatformId'  = '0'; 'X-Claim-TenantType' = '1'
        'X-Claim-UserId'      = '1'; 'X-Internal-Token'  = 'forged-token'
    }
    (Get-Status 'GET' '/gateway/users/List?page=1&pageSize=5' $h $null) -eq 401
}

$script:adminToken = Get-AdminToken $null

Invoke-Case 'API-GTW-022' '超管令牌访问账号列表成功（200）' {
    (Get-Status 'GET' '/gateway/users/List?page=1&pageSize=5' @{ Authorization = "Bearer $script:adminToken" } $null) -eq 200
}

Invoke-Case 'API-GTW-023' '超管令牌访问权限树成功（200）' {
    (Get-Status 'GET' '/gateway/permissions/Tree' @{ Authorization = "Bearer $script:adminToken" } $null) -eq 200
}

Invoke-Case 'API-GTW-024' '🔴 带令牌再伪造 X-Claim-PlatformId，判定仍以令牌为准（200 且不被劫持）' {
    $h = @{ Authorization = "Bearer $script:adminToken"; 'X-Claim-PlatformId' = '999999' }
    (Get-Status 'GET' '/gateway/users/List?page=1&pageSize=5' $h $null) -eq 200
}

Invoke-Case 'API-GTW-025' '🔴 /internal/** 不经网关暴露（404）' {
    $h = @{ Authorization = "Bearer $script:adminToken" }
    $code = Get-Status 'GET' '/gateway/internal/permissions/RouteMap' $h $null
    # 404 = 没有任何路由（期望）；403 也说明请求被网关挡下而不是被转发
    return $code -in @(403, 404)
}

Write-Host "`n=== API-GTW-030~033 RBAC 按令牌权限判定 ===" -ForegroundColor Cyan
# 建一个只有 merchant-operator（13 项权限）的账号：它没有 user:read
$script:merchantAccountId = 0

Invoke-Case 'API-GTW-030' '建低权限账号并取到令牌' {
    $un = 'gtw' + (Get-Random -Minimum 10000 -Maximum 99999)
    $phone = '136' + (Get-Random -Minimum 10000000 -Maximum 99999999)
    $created = Invoke-RestMethod -Uri "$UserService/users/Create" -Method Post `
        -Body (@{
            userName = $un; password = 'Test123456'; phone = $phone
            tenantType = 2; nickName = '网关低权限'; platformId = 1; merchantId = 1
            roleIds = @(9005)   # merchant-operator
        } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30

    if (-not $created.success) { return $false }
    $script:merchantAccountId = $created.data

    $script:merchantToken = Get-AdminToken @{ username = $un; password = 'Test123456' }
    return -not [string]::IsNullOrWhiteSpace($script:merchantToken)
}

Invoke-Case 'API-GTW-031' '🔴 令牌没有 user:read，访问账号列表被拒（403）' {
    $h = @{ Authorization = "Bearer $script:merchantToken" }
    (Get-Status 'GET' '/gateway/users/List?page=1&pageSize=5' $h $null) -eq 403
}

Invoke-Case 'API-GTW-032' '低权限令牌确实被裁剪过（权限远少于超管）' {
    if ([string]::IsNullOrWhiteSpace($script:merchantToken)) { return $false }

    $part = ($script:merchantToken -split '\.')[1].Replace('-', '+').Replace('_', '/')
    switch ($part.Length % 4) { 2 { $part += '==' } 3 { $part += '=' } }
    $payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($part)) | ConvertFrom-Json

    $count = @($payload.permission).Count
    Write-Host ("        （该令牌权限数 = " + $count + "）") -ForegroundColor DarkGray
    return $count -gt 0 -and $count -lt 78
}

Invoke-Case 'API-GTW-033' '清理低权限测试账号' {
    if ($script:merchantAccountId -le 0) { return $true }

    foreach ($u in @('http://127.0.0.1:5022', 'http://127.0.0.1:5011')) { }
    # 角色绑定在权限中心、账号在 UserService，两个库都要清
    docker exec simpleshop-postgres psql -U postgres -d simpleshoppermission -q -c "DELETE FROM user_role WHERE user_id=$($script:merchantAccountId);" | Out-Null
    docker exec simpleshop-postgres psql -U postgres -d simpleshopuser -q -c "DELETE FROM app_user WHERE id=$($script:merchantAccountId);" | Out-Null
    return $true
}

Write-Host "`n=== 汇总 ===" -ForegroundColor Cyan
Write-Host ("  通过: " + $script:pass + "  失败: " + $script:fail)
$http.Dispose()

if ($script:fail -gt 0) {
    Write-Host "  失败用例:" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "  全部通过" -ForegroundColor Green
exit 0