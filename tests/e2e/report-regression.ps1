<#
.SYNOPSIS
    报表（工作台经营报表）回归测试。
.DESCRIPTION
    报表的错都是「看起来对、其实口径错」那一类，所以重点不在「接口通不通」，
    而在每个指标的口径是否与 BUSINESS.md 17 一致：
      - GMV 按支付时间统计，且不含已取消与已退款
      - 客单价 = GMV / 支付订单数，分母为 0 时是 0 而不是 NaN
      - 退款金额只算审批通过的，按审批时间落在区间内
      - 时间范围四档固定，非法档位被校验挡住而不是 500
    退出码非 0 即视为回归失败。
#>
[CmdletBinding()]
param(
    [string]$OrderService = 'http://127.0.0.1:5064',
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
            if ($StopOnFail) { throw "用例 $Id 失败" }
        }
    } catch {
        $script:fail++
        $script:failures += "$Id $Name (异常: $($_.Exception.Message))"
        Write-Host ("  FAIL  " + $Id + "  " + $Name + "  " + $_.Exception.Message) -ForegroundColor Red
        if ($StopOnFail) { throw }
    }
}

function Report([int]$Range, [long]$MerchantId = 0) {
    return Invoke-RestMethod -Uri "$OrderService/reports/Report" -Method Post `
        -Body (@{ range = $Range; merchantId = $MerchantId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
}

# 校验失败时 Invoke-RestMethod 会抛异常，响应体（真正的失败原因）在 ErrorDetails.Message。
# 这里把它读成对象返回，用例才能断言「是哪个字段的规则拦的」。
function PostExpectingReject([hashtable]$Body) {
    try {
        Invoke-RestMethod -Uri "$OrderService/reports/Report" -Method Post `
            -Body ($Body | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 20 | Out-Null
        return $null
    } catch {
        $code = 0
        if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
        return [pscustomobject]@{ StatusCode = $code; Body = $_.ErrorDetails.Message }
    }
}

Write-Host "`n=== RPT 可达性 ===" -ForegroundColor Cyan

$reachable = $false
try { $reachable = (Invoke-WebRequest "$OrderService/health" -UseBasicParsing -TimeoutSec 10).StatusCode -eq 200 } catch { }
if (-not $reachable) {
    Write-Host "  OrderService($OrderService) 不可达，请先执行 ./scripts/start-services.ps1" -ForegroundColor Red
    Write-Host "通过: 0  失败: 1" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RPT 四档时间范围 ===" -ForegroundColor Cyan

$script:expectedNames = @{ 1 = '今日'; 2 = '昨日'; 3 = '近 7 天'; 4 = '近 30 天' }

foreach ($range in 1, 2, 3, 4) {
    Invoke-Case ("API-RPT-{0:D3}" -f $range) "档位 $range 返回中文名「$($script:expectedNames[$range])」且区间左闭右开" {
        $r = Report $range
        if (-not $r.success) { return $false }
        if ($r.data.range -ne $range) { return $false }
        if ($r.data.rangeName -ne $script:expectedNames[$range]) { return $false }
        return $r.data.to -gt $r.data.from
    }
}

Invoke-Case 'API-RPT-005' '🔴 非法档位 99 被校验挡住（400 而不是 500）' {
    $r = PostExpectingReject @{ range = 99 }
    if ($null -eq $r) { return $false }
    return $r.StatusCode -eq 400 -and $r.Body -match 'Range'
}

Invoke-Case 'API-RPT-006' '🔴 档位 0 被拒（不能静默按默认档算一个数出来）' {
    $r = PostExpectingReject @{ range = 0 }
    if ($null -eq $r) { return $false }
    return $r.StatusCode -eq 400
}

Write-Host "`n=== RPT 指标口径 ===" -ForegroundColor Cyan

$script:r30 = Report 4

Invoke-Case 'API-RPT-010' '八项指标全部存在' {
    $fields = @('gmv', 'orderCount', 'paidOrderCount', 'completedOrderCount',
                'avgOrderValue', 'refundAmount', 'refundRate', 'lowStockCount')
    foreach ($f in $fields) {
        if (-not ($script:r30.data.PSObject.Properties.Name -contains $f)) { return $false }
    }
    return $true
}

Invoke-Case 'API-RPT-011' '支付订单数不超过订单数（口径没写反）' {
    return $script:r30.data.paidOrderCount -le $script:r30.data.orderCount
}

Invoke-Case 'API-RPT-012' '完成订单数不超过支付订单数' {
    return $script:r30.data.completedOrderCount -le $script:r30.data.paidOrderCount
}

Invoke-Case 'API-RPT-013' '🔴 客单价 = GMV / 支付订单数（按原始值自行复算）' {
    $d = $script:r30.data
    if ($d.paidOrderCount -le 0) {
        Write-Host "        （区间内无支付订单，跳过复算）" -ForegroundColor DarkGray
        return $d.avgOrderValue -eq 0
    }
    $expected = [math]::Round($d.gmv / $d.paidOrderCount, 2)
    return [math]::Abs($d.avgOrderValue - $expected) -lt 0.01
}

Invoke-Case 'API-RPT-014' '🔴 退款率 = 退款金额 / GMV，且是 0~1 的小数而非百分数' {
    $d = $script:r30.data
    if ($d.gmv -le 0) {
        Write-Host "        （区间内 GMV 为 0，跳过复算）" -ForegroundColor DarkGray
        return $d.refundRate -eq 0
    }
    $expected = [math]::Round($d.refundAmount / $d.gmv, 4)
    if ([math]::Abs($d.refundRate - $expected) -ge 0.0001) { return $false }
    return $d.refundRate -le 1
}

Invoke-Case 'API-RPT-015' '所有金额非负（没有出现负 GMV / 负退款）' {
    $d = $script:r30.data
    return ($d.gmv -ge 0) -and ($d.refundAmount -ge 0) -and ($d.avgOrderValue -ge 0)
}

Invoke-Case 'API-RPT-016' '今日区间落在近 30 天区间内（区间换算没算错）' {
    $today = Report 1
    if ($today.data.from -lt $script:r30.data.from) { return $false }
    return $today.data.to -le $script:r30.data.to
}

Invoke-Case 'API-RPT-017' '昨日与今日区间不重叠（左右都闭会在午夜重复计数）' {
    $today = Report 1
    $yesterday = Report 2
    return $yesterday.data.to -le $today.data.from
}

Write-Host "`n=== RPT 租户过滤与校验 ===" -ForegroundColor Cyan

Invoke-Case 'API-RPT-020' '按不存在的商户过滤不报错，且订单数不超过全量' {
    $r = Report 4 999999999
    if (-not $r.success) { return $false }
    return $r.data.orderCount -le $script:r30.data.orderCount
}

Invoke-Case 'API-RPT-021' '🔴 负数 merchantId 被 MerchantId 规则挡住' {
    $r = PostExpectingReject @{ range = 4; merchantId = -1 }
    if ($null -eq $r) { return $false }
    return $r.StatusCode -eq 400 -and $r.Body -match 'MerchantId'
}

Write-Host ""
Write-Host "通过: $script:pass  失败: $script:fail" -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
if ($script:fail -gt 0) {
    Write-Host "失败用例:" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
exit 0
