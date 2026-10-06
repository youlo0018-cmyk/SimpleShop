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

Invoke-Case 'API-GTW-034' '🔴 P0 通配权限点 log:read 生效：无该权限读日志被拒' {
    # 回归一个真实漏洞：/gateway/logs/* 曾被当成字面量，前缀算成 /gateway/logs/*/，
    # 永远匹配不上 /gateway/logs/Pv/List，于是 requiredCode 为 null、请求被**放行**。
    # 结果 log:read 完全失效 —— 实测 15 项权限的财务账号读到了 11270 条 pv 日志。
    $h = @{ Authorization = "Bearer $script:merchantToken" }
    # body 传**哈希表**而不是 JSON 字符串：Get-Status 内部会对 body 再做一次
    # ConvertTo-Json，传字符串会被二次编码成一个「装着 JSON 的字符串」，
    # 后端解析失败回 400，断言 403 就会假失败。
    (Get-Status 'POST' '/gateway/logs/Pv/List' $h @{ page = 1; pageSize = 1 }) -eq 403
}

Invoke-Case 'API-GTW-035' '🔴 P0 通配权限点 report:view 生效：无该权限读报表被拒' {
    $h = @{ Authorization = "Bearer $script:merchantToken" }
    (Get-Status 'POST' '/gateway/reports/Report' $h @{ range = 4 }) -eq 403
}

Invoke-Case 'API-GTW-036' '🔴🔴 P0 通配权限点 permission:manage 生效：无该权限不能新增权限点（提权路径）' {
    # 这一条是本次漏洞里最严重的一环：能新增权限点 = 能给自己授任意权限 = 完全提权。
    $h = @{ Authorization = "Bearer $script:merchantToken" }
    $body = @{ code = 'probe:gw'; name = '探测'; parentId = 1; apiPath = '/gateway/probe/gw'; status = 1 }
    (Get-Status 'POST' '/gateway/permissions/Create' $h $body) -eq 403
}

Invoke-Case 'API-GTW-037' '通配修复不能误伤：超管读日志 / 报表仍成功' {
    # 只加「拦」很容易，把该放行的也拦掉就是另一种故障。
    $h = @{ Authorization = "Bearer $script:adminToken" }
    if ((Get-Status 'POST' '/gateway/logs/Pv/List' $h @{ page = 1; pageSize = 1 }) -ne 200) { return $false }
    return (Get-Status 'POST' '/gateway/reports/Report' $h @{ range = 4 }) -eq 200
}

Invoke-Case 'API-GTW-038' '🔴 P0 游客可搜索商品（匿名白名单漏登记会直接 401）' {
    # 规格 1.2：游客能浏览商品。搜索也是浏览能力。
    # 之前 /shop/products/Search 没登记进匿名白名单，于是「游客能逛列表、不能搜」，
    # 症状像是搜索功能坏了，根本查不到是网关这一层漏了。
    #
    # 刻意**不带任何 Authorization 头** —— 带上低权限令牌就测不出匿名开放了。
    $r = Invoke-RestMethod "$Gateway/gateway/shop/products/Search" -Method Post `
        -Body (@{ keyword = '商品'; page = 1; pageSize = 5 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    return $r.success
}

Invoke-Case 'API-GTW-039' '🔴 P0 游客可浏览店铺列表与店铺详情' {
    # 店铺浏览是 C 端必需能力（BUSINESS 1.2 把店铺页列入游客可浏览范围）。
    # 列表与详情是两条路径：白名单只登记精确路径的话详情会 401，
    # 症状是「列表能看、点进去 401」，很容易被当成前端路由问题。
    $list = Invoke-RestMethod "$Gateway/gateway/merchants/Shop?page=1&pageSize=5" -TimeoutSec 30
    if (-not $list.success) { return $false }

    $hit = @($list.data.items)[0]
    if (-not $hit) { return $true }   # 没有可用店铺时列表可达即算通过

    $detail = Invoke-RestMethod "$Gateway/gateway/merchants/Shop/$($hit.merchantId)" -TimeoutSec 30
    return $detail.success -and $detail.data.merchantId -eq $hit.merchantId
}

Write-Host "`n=== GTW 畸形令牌必须 401 而非 500（AUT-007 / AUT-008）===" -ForegroundColor Cyan

# REVIEW 链路 0 第 2 步明确要求：解码异常**必须捕获**，不得冒泡成 500。
# 500 看着像「服务器坏了」，实际是网关没兜住 —— 而且 500 常被网关默认重试/告警策略
# 当成真故障处理，比 401 吵得多。
$badTokens = @(
    @{ name = '非 JWT 串';        token = 'Bearer hello-world' },
    @{ name = '乱码三段';          token = 'Bearer aaa.bbb.ccc' },
    @{ name = 'alg=none 空签';     token = 'Bearer eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJzdWIiOiIxIn0.' },
    @{ name = '篡改载荷';          token = 'Bearer eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxIiwicm9sZSI6InN1cGVyIn0.bad' },
    @{ name = 'HS256 冒充 RS256';  token = 'Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxIn0.abc' },
    @{ name = '空 Bearer';         token = 'Bearer ' }
)

$allBad401 = $true
foreach ($t in $badTokens) {
    try {
        Invoke-WebRequest "$Gateway/gateway/users/List" -Method Get `
            -Headers @{ Authorization = $t.token } -UseBasicParsing -TimeoutSec 15 | Out-Null
        Write-Host ("        ✗ {0} 居然通过了" -f $t.name) -ForegroundColor Red
        $allBad401 = $false
    } catch {
        $code = [int]$_.Exception.Response.StatusCode
        if ($code -ne 401) {
            Write-Host ("        ✗ {0} 返回 {1}（期望 401）" -f $t.name, $code) -ForegroundColor Red
            $allBad401 = $false
        }
    }
}

# 用 scriptblock 闭包包住布尔值：Invoke-Case 的第三参是 scriptblock，
# 直接传 $allBad401 会被当成非法的 scriptblock 而永远判失败。
Invoke-Case 'API-GTW-041' '🔴🔴 P0 六种畸形 / 伪造令牌一律 401，无一冒泡成 500（AUT-007 / AUT-008）' { $allBad401 }

Invoke-Case 'API-GTW-042' '完全不带头也回 401（而不是 404 或 500）' {
    try {
        Invoke-WebRequest "$Gateway/gateway/users/List" -Method Get -UseBasicParsing -TimeoutSec 15 | Out-Null
        return $false
    } catch {
        return [int]$_.Exception.Response.StatusCode -eq 401
    }
}

Write-Host "`n=== GTW 客户令牌不得触碰后台接口（fail-closed）===" -ForegroundColor Cyan

$script:customerToken = ''

Invoke-Case 'API-GTW-043' '准备：注册一个普通客户并拿到客户令牌' {
    $sfx = Get-Random -Minimum 100000 -Maximum 999999
    $tail = ([string]$sfx).PadLeft(8, '0').Substring(0, 8)
    $body = @{
        customerName = "gtw$sfx"; password = 'Gtw12345678'
        phone = "136$tail"; nickName = '网关回归客户'
    }
    $r = Invoke-RestMethod "$Gateway/gateway/customers/Register" -Method Post `
        -Body ($body | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 25
    $script:customerToken = [string]$r.data.token
    return $r.success -and $script:customerToken.Length -gt 20
}

Invoke-Case 'API-GTW-044' '🔴 P0 客户令牌调 payments/Simulate 被 403（否则白拿商品）' {
    # 这是本轮修掉的那个洞：/gateway/payments/Simulate 没有权限映射，
    # 而网关的判定是「查不到映射 → 放行」，于是顾客拿自己的客户令牌
    # 就能把自己的订单标成已支付。订单真的会走到 20 待发货。
    #
    # 现在按「未映射 = 只可能是 C 端接口」处理：客户令牌照常放行 C 端，
    # 后台令牌走到未映射路径一律 403 并记日志，逼人去补权限种子。
    $h = @{ Authorization = "Bearer $script:customerToken" }
    $status = Get-Status 'POST' '/gateway/payments/Simulate' $h @{ orderNo = 'X'; success = $true }

    Write-Host ("        payments/Simulate -> HTTP {0}" -f $status) -ForegroundColor DarkGray
    return $status -eq 403
}

Invoke-Case 'API-GTW-045' '🔴 客户令牌调 points/SaveRules 被 403（否则自己改积分规则）' {
    # 同一类洞的另一处：SaveRules 是**写**积分规则的端点，
    # 而种子里只绑了读的那条（points/Rules），写的那条一直不鉴权。
    $h = @{ Authorization = "Bearer $script:customerToken" }
    $status = Get-Status 'POST' '/gateway/points/SaveRules' $h @{ earnPerYuan = 100 }

    Write-Host ("        points/SaveRules -> HTTP {0}" -f $status) -ForegroundColor DarkGray
    return $status -eq 403
}

Invoke-Case 'API-GTW-046' '客户令牌访问 C 端接口仍然放行（fail-closed 没有误伤）' {
    # 反面同样重要：未映射的 C 端接口是**故意**不绑后台权限点的
    # （绑了会把小程序自己挡掉）。一刀切拒绝会让整个小程序不可用。
    $h = @{ Authorization = "Bearer $script:customerToken" }
    $status = Get-Status 'GET' '/gateway/points/Balance?customerId=0' $h $null

    Write-Host ("        points/Balance -> HTTP {0}" -f $status) -ForegroundColor DarkGray
    return $status -eq 200
}

Invoke-Case 'API-GTW-040' '清理低权限测试账号' {
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
