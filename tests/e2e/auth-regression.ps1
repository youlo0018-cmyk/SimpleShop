<#
.SYNOPSIS
    AuthService（后台令牌服务）回归测试。
.DESCRIPTION
    覆盖 password flow 的成功路径与全部拒绝路径，重点是几条安全不变量：
      - 令牌必须是 RS256，且带齐 tenant_type / platform_id / permission / role
      - 权限点必须**全部**进令牌（曾经因为用 SetClaim 而不是 AddClaim，78 项只剩 1 项）
      - 密码错误与账号不存在必须返回**逐字节相同**的响应（防账号枚举）
      - 客户账号不得走后台登录（账号域互斥，BUSINESS 4.2）
      - 停用账号不得登录
    退出码非 0 即视为回归失败。
#>
[CmdletBinding()]
param(
    [string]$AuthService = 'http://127.0.0.1:5019',
    [string]$UserService = 'http://127.0.0.1:5011',
    [string]$CustomerService = 'http://127.0.0.1:5280',
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

# 直接用 HttpClient 读原始响应体：Invoke-RestMethod 在错误分支上拿不到 body，
# 而「两个失败分支的响应体是否逐字节相同」正是本脚本要断言的关键点。
Add-Type -AssemblyName System.Net.Http
$http = [System.Net.Http.HttpClient]::new()

function Get-RawToken([string]$body) {
    $content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, 'application/x-www-form-urlencoded')
    $resp = $http.PostAsync("$AuthService/connect/token", $content).GetAwaiter().GetResult()
    return [pscustomobject]@{
        Status  = [int]$resp.StatusCode
        Body    = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
}

function Decode-Part([string]$part) {
    $s = $part.Replace('-', '+').Replace('_', '/')
    switch ($s.Length % 4) { 2 { $s += '==' } 3 { $s += '=' } }
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($s))
}

function Invoke-Json([string]$url, [hashtable]$payload) {
    return Invoke-RestMethod -Uri $url -Method Post -Body ($payload | ConvertTo-Json -Compress) `
        -ContentType 'application/json' -TimeoutSec 30
}

Write-Host "`n=== ATH 健康检查 ===" -ForegroundColor Cyan
Invoke-Case 'API-ATH-001' 'AuthService /health 返回 200' {
    # MapHealthChecks 默认回的是纯文本 Healthy，不是 JSON——别用 .status 去取
    ((Invoke-WebRequest "$AuthService/health" -TimeoutSec 10 -UseBasicParsing).Content -match 'Healthy')
}

Write-Host "`n=== API-ATH-010~012 令牌签发 ===" -ForegroundColor Cyan
$okBody = "grant_type=password&client_id=admin-app&username=$AdminUser&password=$AdminPassword"
$script:token = $null

Invoke-Case 'API-ATH-010' '正确凭据换到 access_token' {
    $r = Get-RawToken $okBody
    if ($r.Status -ne 200) { return $false }
    $json = $r.Body | ConvertFrom-Json
    $script:token = $json.access_token
    return $null -ne $script:token
}

Invoke-Case 'API-ATH-011' 'token_type 为 Bearer，expires_in 约等于配置的 2 小时' {
    $json = (Get-RawToken $okBody).Body | ConvertFrom-Json
    return $json.token_type -eq 'Bearer' -and $json.expires_in -gt 7000 -and $json.expires_in -le 7200
}

Invoke-Case 'API-ATH-012' '🔴 算法必须是 RS256（网关只认 RS256）' {
    $header = Decode-Part (($script:token -split '\.')[0]) | ConvertFrom-Json
    return $header.alg -eq 'RS256'
}

Write-Host "`n=== API-ATH-020~026 令牌内容 ===" -ForegroundColor Cyan
$script:payload = Decode-Part (($script:token -split '\.')[1]) | ConvertFrom-Json

Invoke-Case 'API-ATH-020' '带齐租户与操作人声明' {
    $p = $script:payload
    return $null -ne $p.sub -and $null -ne $p.tenant_type -and
           $null -ne $p.platform_id -and $null -ne $p.merchant_id -and $null -ne $p.user_name
}

Invoke-Case 'API-ATH-021' '超管的 platform_id 为 0（不受平台限制）' {
    return $script:payload.tenant_type -eq '1' -and $script:payload.platform_id -eq '0'
}

Invoke-Case 'API-ATH-022' '🔴 权限点必须全部进令牌（曾因 SetClaim 只剩最后一个）' {
    return $script:payload.permission.Count -ge 70
}

Invoke-Case 'API-ATH-023' '令牌里含具体权限点' {
    return $script:payload.permission -contains 'user:read' -and
           $script:payload.permission -contains 'order:ship'
}

Invoke-Case 'API-ATH-024' '角色声明存在' {
    return $script:payload.role -contains 'platform-admin'
}

Write-Host "`n=== API-ATH-030~038 拒绝路径（安全不变量）===" -ForegroundColor Cyan
$script:wrongPwd = $null
$script:notFound = $null

Invoke-Case 'API-ATH-030' '密码错误被拒' {
    $r = Get-RawToken "grant_type=password&client_id=admin-app&username=$AdminUser&password=definitely-wrong-123"
    $script:wrongPwd = $r
    return $r.Status -eq 400 -and $r.Body -match 'invalid_grant'
}

Invoke-Case 'API-ATH-031' '账号不存在被拒' {
    $r = Get-RawToken "grant_type=password&client_id=admin-app&username=no_such_admin&password=$AdminPassword"
    $script:notFound = $r
    return $r.Status -eq 400 -and $r.Body -match 'invalid_grant'
}

Invoke-Case 'API-ATH-032' '🔴 密码错误与账号不存在返回逐字节相同的响应（防账号枚举）' {
    return ($null -ne $script:wrongPwd) -and ($null -ne $script:notFound) -and
           ($script:wrongPwd.Status -eq $script:notFound.Status) -and
           ($script:wrongPwd.Body -eq $script:notFound.Body)
}

Invoke-Case 'API-ATH-033' '缺少密码参数被拒' {
    $r = Get-RawToken "grant_type=password&client_id=admin-app&username=$AdminUser&password="
    return $r.Status -eq 400
}

Invoke-Case 'API-ATH-034' '未知 client_id 被拒（401 invalid_client）' {
    $r = Get-RawToken "grant_type=password&client_id=evil-app&username=$AdminUser&password=$AdminPassword"
    return $r.Status -eq 401 -and $r.Body -match 'invalid_client'
}

Invoke-Case 'API-ATH-035' 'client_credentials 等不支持的 grant 被拒' {
    $r = Get-RawToken 'grant_type=client_credentials&client_id=admin-app'
    return $r.Status -ge 400
}

Invoke-Case 'API-ATH-036' '🔴 客户账号不得走后台登录（账号域互斥）' {
    $phone = '139' + (Get-Random -Minimum 10000000 -Maximum 99999999)
    $name = "cst$phone"
    Invoke-Json "$CustomerService/customers/Register" @{
        customerName = $name; password = 'Test123456'; phone = $phone; nickName = '客户'
    } | Out-Null
    $r = Get-RawToken "grant_type=password&client_id=admin-app&username=$name&password=Test123456"
    return $r.Status -eq 400 -and $r.Body -match 'invalid_grant'
}

Invoke-Case 'API-ATH-037' '🔴 停用账号不得登录，恢复后可登录' {
    # users/List 是 GET + 查询参数，不是 POST——用 Invoke-Json 会拿到 405
    $uid = (Invoke-RestMethod "$UserService/users/List?page=1&pageSize=50&keyword=$AdminUser" -TimeoutSec 30).data |
        Where-Object { $_.userName -eq $AdminUser } | Select-Object -First 1
    if ($null -eq $uid) { return $false }
    $id = [long]$uid.id

    Invoke-Json "$UserService/users/UpdateStatus" @{ userId = $id; status = 2 } | Out-Null
    $blocked = Get-RawToken $okBody

    Invoke-Json "$UserService/users/UpdateStatus" @{ userId = $id; status = 1 } | Out-Null
    $restored = Get-RawToken $okBody

    return $blocked.Status -eq 400 -and $blocked.Body -match 'disabled' -and $restored.Status -eq 200
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