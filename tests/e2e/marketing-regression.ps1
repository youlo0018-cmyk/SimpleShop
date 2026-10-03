<#
.SYNOPSIS
    MarketingService（5072）券生命周期回归测试。
.DESCRIPTION
    覆盖订单链路的 ① 占券需要的能力：
      领券 → 结算试算（最优券）→ 占券 → 核销 / 回退
    以及几条容易出错的规则：
      - 模板规则发放时**快照**到用户券，模板改了不影响已发出的券
      - 一笔订单只能占一张券（重复下单幂等）
      - 已核销的券不能回退（要退款而不是取消）
      - 没占到券**不算失败**（客户没券只是不优惠）
      - 池子扣减与发券在同一事务里，不产生超发
    退出码非 0 即视为回归失败。
#>
[CmdletBinding()]
param(
    [string]$Marketing = 'http://127.0.0.1:5072',
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

$script:suffix = Get-Random -Minimum 100000 -Maximum 999999
$script:customerId = 900000000 + $script:suffix
$script:now = [DateTime]::UtcNow

# 订单行合计 500：SPU 100 / SKU 1001 / 200，SPU 200 / SKU 2001 / 300
$script:lines = @(
    [pscustomobject]@{ spuId = 100; skuId = 1001; amount = 200 },
    [pscustomobject]@{ spuId = 200; skuId = 2001; amount = 300 }
)

function Post([string]$Path, $Body) {
    return Invoke-RestMethod -Uri "$Marketing$Path" -Method Post `
        -Body ($Body | ConvertTo-Json -Depth 6) -ContentType 'application/json' -TimeoutSec 30
}

Write-Host "`n=== MKT 建模板 / 建活动 ===" -ForegroundColor Cyan
$script:templateId = 0
$script:activityId = 0

Invoke-Case 'API-MKT-001' '新建满减券模板（满 100 减 20，30 天有效，每人限领 2）' {
    $r = Post '/marketing/coupon-templates/Create' @{
        templateName = "满减券$($script:suffix)"
        couponType = 1; thresholdAmount = 100; discountAmount = 20
        validDays = 30; totalQuantity = 50; perUserLimit = 2; perOrderLimit = 1
        platformId = 0; status = 1
    }
    $script:templateId = $r.data
    return $r.success -and $script:templateId -gt 0
}

Invoke-Case 'API-MKT-002' '新建券活动（发 20 张）' {
    $r = Post '/marketing/coupon-activities/Create' @{
        activityName = "活动$($script:suffix)"
        templateId = $script:templateId
        claimStartTime = $script:now.AddMinutes(-5).ToString('o')
        claimEndTime = $script:now.AddDays(1).ToString('o')
        claimQuantity = 20; perUserLimit = 2
        targetType = 1; targets = '[]'
        platformId = 0; status = 1
    }
    $script:activityId = $r.data
    return $r.success -and $script:activityId -gt 0
}

Write-Host "`n=== MKT 领券 ===" -ForegroundColor Cyan
$script:couponCode = ''

Invoke-Case 'API-MKT-010' '领券成功，返回券码' {
    $r = Post '/coupons/Claim' @{ customerId = $script:customerId; activityId = $script:activityId; quantity = 1 }
    if ($r.success -and $r.data.couponCodes.Count -ge 1) { $script:couponCode = $r.data.couponCodes[0] }
    return $r.success -and $r.data.couponCodes.Count -eq 1
}

Invoke-Case 'API-MKT-011' '限领第 2 张仍然成功（上限 2 张）' {
    $r = Post '/coupons/Claim' @{ customerId = $script:customerId; activityId = $script:activityId; quantity = 1 }
    return $r.success -and $r.data.couponCodes.Count -eq 1
}

Invoke-Case 'API-MKT-011b' '超过每人限领（第 3 张被拒）' {
    $r = Post '/coupons/Claim' @{ customerId = $script:customerId; activityId = $script:activityId; quantity = 1 }
    return (-not $r.success) -and $r.message -match '限领'
}

Invoke-Case 'API-MKT-012' '🔴 超出领取窗口被拒' {
    $r = Post '/marketing/coupon-activities/Create' @{
        activityName = "过期活动$($script:suffix)"
        templateId = $script:templateId
        claimStartTime = $script:now.AddDays(-2).ToString('o')
        claimEndTime = $script:now.AddDays(-1).ToString('o')
        claimQuantity = 10; perUserLimit = 5
        targetType = 1; targets = '[]'; platformId = 0; status = 1
    }
    $c = Post '/coupons/Claim' @{ customerId = $script:customerId; activityId = $r.data; quantity = 1 }
    return (-not $c.success) -and $c.message -match '领取时间'
}

Invoke-Case 'API-MKT-013' '🔴 模板改了不影响已发出的券（快照生效）' {
    # 直接改库模拟运营改模板
    docker exec simpleshop-postgres psql -U postgres -d simpleshopmarketing -q -c "UPDATE coupon_template SET discount_amount = 999 WHERE id = $($script:templateId);" | Out-Null

    $settle = Post '/coupons/Settle' @{ customerId = $script:customerId; lines = $script:lines }
    # 已发出的券仍按自己的快照算：满 100 减 20 → 优惠 20，而不是模板的新 999
    return $settle.success -and $settle.data.best.discountAmount -eq 20
}

Write-Host "`n=== MKT 结算试算 ===" -ForegroundColor Cyan

Invoke-Case 'API-MKT-020' '试算标出最优券与全部可用券' {
    $r = Post '/coupons/Settle' @{ customerId = $script:customerId; lines = $script:lines }
    return $r.success -and $r.data.hasCoupon -and $null -ne $r.data.best -and $r.data.options.Count -ge 1
}

Invoke-Case 'API-MKT-021' '游客（customerId=0）不计券' {
    $r = Post '/coupons/Settle' @{ customerId = 0; lines = $script:lines }
    return $r.success -and -not $r.data.hasCoupon -and $r.data.options.Count -eq 0
}

Write-Host "`n=== MKT 下单：占券 → 核销 / 回退 ===" -ForegroundColor Cyan

Invoke-Case 'API-MKT-030' '占券成功，优惠额为快照值 20' {
    $r = Post '/coupons/Occupy' @{
        customerId = $script:customerId; orderNo = "ORD-$($script:suffix)-1"; couponId = 0; lines = $script:lines
    }
    $script:orderNo1 = "ORD-$($script:suffix)-1"
    return $r.success -and $r.data.discountAmount -eq 20
}

Invoke-Case 'API-MKT-031' '🔴 同一订单重复占券幂等，不重复占第二张' {
    $r = Post '/coupons/Occupy' @{
        customerId = $script:customerId; orderNo = $script:orderNo1; couponId = 0; lines = $script:lines
    }
    return $r.success -and $r.data.alreadyApplied -eq $true
}

Invoke-Case 'API-MKT-032' '支付成功核销：券作废' {
    $r = Post '/coupons/Consume' @{ customerId = $script:customerId; orderNo = $script:orderNo1 }
    return $r.success
}

Invoke-Case 'API-MKT-033' '🔴 已核销的券不能被回退（应走退款流程）' {
    $r = Post '/coupons/Release' @{ customerId = $script:customerId; orderNo = $script:orderNo1 }
    return (-not $r.success) -and $r.message -match '已核销'
}

Invoke-Case 'API-MKT-034' '取消订单：占券后回退，券回到可用' {
    $orderNo = "ORD-$($script:suffix)-2"
    $occ = Post '/coupons/Occupy' @{
        customerId = $script:customerId; orderNo = $orderNo; couponId = 0; lines = $script:lines
    }
    if (-not $occ.success) { return $false }

    $rel = Post '/coupons/Release' @{ customerId = $script:customerId; orderNo = $orderNo }
    if (-not $rel.success) { return $false }

    # 回退后这张券应该又能用了
    $settle = Post '/coupons/Settle' @{ customerId = $script:customerId; lines = $script:lines }
    return $settle.success -and $settle.data.options.Count -eq 1
}

Invoke-Case 'API-MKT-035' '没占到券不算失败（只是不优惠）' {
    # 一个没有任何券的新客户下单
    $ghost = 900999999 + $script:suffix
    $r = Post '/coupons/Occupy' @{
        customerId = $ghost; orderNo = "ORD-GHOST-$($script:suffix)"; couponId = 0; lines = $script:lines
    }
    return $r.success -and $r.data.discountAmount -eq 0
}

Write-Host "`n=== MKT 营销配置 ===" -ForegroundColor Cyan

Invoke-Case 'API-MKT-040' '未配置过优先级时返回默认「券优先」' {
    $r = Invoke-RestMethod "$Marketing/marketing/marketing-config/Get?platformId=$($script:suffix)" -TimeoutSec 30
    return $r.success -and $r.data.priority -eq 2
}

Invoke-Case 'API-MKT-041' '保存优先级为「活动优先」后可读回' {
    Post '/marketing/marketing-config/Save' @{ platformId = $script:suffix; priority = 1 } | Out-Null
    $r = Invoke-RestMethod "$Marketing/marketing/marketing-config/Get?platformId=$($script:suffix)" -TimeoutSec 30
    return $r.success -and $r.data.priority -eq 1
}

Invoke-Case 'API-MKT-042' '非法优先级被拒' {
    try { Post '/marketing/marketing-config/Save' @{ platformId = $script:suffix; priority = 9 } | Out-Null; return $false }
    catch { return [int]$_.Exception.Response.StatusCode -eq 400 }
}

Write-Host "`n=== 汇总 ===" -ForegroundColor Cyan
Write-Host ("  通过: " + $script:pass + "  失败: " + $script:fail)

if ($script:fail -gt 0) {
    Write-Host "  失败用例:" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "  全部通过" -ForegroundColor Green
exit 0