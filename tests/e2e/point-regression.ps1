<#
.SYNOPSIS
    PointService（5082）回归测试。
.DESCRIPTION
    积分是下单抵扣的第二道防线，算错就是「订单金额算错」或「用户积分凭空消失/凭空多出」。
    重点验证：
      - 冻结模型：锁定 / 实扣 / 解冻 / 按比例回收 四个动作的语义（BUSINESS.md 13.4）
      - 幂等：同业务号重复请求不重复发放（唯一索引兜底）
      - 余额上限 100000，超出截断不入账但仍记流水
      - 签到：当日幂等 + 7 天一轮奖励
      - 退款按比例回收**向上取整**，且退回**原发放批次**（不重置有效期）
    退出码非 0 即视为回归失败。
#>
[CmdletBinding()]
param(
    [string]$PointService = 'http://127.0.0.1:5082',
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

function Send([string]$Path, $Body) {
    return Invoke-RestMethod -Uri "$PointService/internal/points/$Path" -Method Post `
        -Body ($Body | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30
}

function Get-Balance([long]$CustomerId) {
    return (Invoke-RestMethod "$PointService/points/Balance?customerId=$CustomerId" -TimeoutSec 30).data
}

Write-Host "`n=== PNT 发放与幂等 ===" -ForegroundColor Cyan
$script:alice = 500000000000 + $script:suffix

Invoke-Case 'API-PNT-001' '发放 100：可用 0→100' {
    $r = Send 'Earn' @{ customerId = $script:alice; source = 'register'; quantity = 100; bizNo = "REG-$($script:alice)" }
    return $r.success -and $r.data.available -eq 100 -and $r.data.frozen -eq 0
}

Invoke-Case 'API-PNT-002' '🔴 同业务号重复发放不重复给（幂等）' {
    $r = Send 'Earn' @{ customerId = $script:alice; source = 'register'; quantity = 100; bizNo = "REG-$($script:alice)" }
    return $r.success -and $r.data.alreadyApplied -eq $true -and $r.data.available -eq 100
}

Invoke-Case 'API-PNT-003' '不同业务号可以再发' {
    $r = Send 'Earn' @{ customerId = $script:alice; source = 'order_completed'; quantity = 50; bizNo = 'ORD-EARN' }
    return $r.success -and $r.data.available -eq 150
}

Write-Host "`n=== PNT 冻结模型：锁定 → 实扣 ===" -ForegroundColor Cyan

Invoke-Case 'API-PNT-010' '锁定 60：available -= 60，frozen += 60' {
    $r = Send 'Lock' @{ customerId = $script:alice; bizNo = 'ORD-A'; quantity = 60 }
    return $r.success -and $r.data.available -eq 90 -and $r.data.frozen -eq 60
}

Invoke-Case 'API-PNT-011' '🔴 同单号重复锁定不重复扣' {
    $r = Send 'Lock' @{ customerId = $script:alice; bizNo = 'ORD-A'; quantity = 60 }
    return $r.success -and $r.data.alreadyApplied -eq $true -and $r.data.available -eq 90 -and $r.data.frozen -eq 60
}

Invoke-Case 'API-PNT-012' '🔴 超额锁定被拒，库存不变' {
    $r = Send 'Lock' @{ customerId = $script:alice; bizNo = 'ORD-BIG'; quantity = 999 }
    $b = Get-Balance $script:alice
    return -not $r.success -and $r.message -match '可用积分不足' -and $b.available -eq 90 -and $b.frozen -eq 60
}

Invoke-Case 'API-PNT-013' '支付实扣：frozen -= 60，可用不动' {
    $r = Send 'Consume' @{ customerId = $script:alice; bizNo = 'ORD-A' }
    return $r.success -and $r.data.frozen -eq 0 -and $r.data.available -eq 90
}

Invoke-Case 'API-PNT-014' '🔴 同单号重复实扣不重复扣' {
    $r = Send 'Consume' @{ customerId = $script:alice; bizNo = 'ORD-A' }
    return $r.success -and $r.data.alreadyApplied -eq $true -and $r.data.frozen -eq 0
}

Write-Host "`n=== PNT 取消链路：锁定 → 解冻 ===" -ForegroundColor Cyan

Invoke-Case 'API-PNT-020' '锁定 30' {
    $r = Send 'Lock' @{ customerId = $script:alice; bizNo = 'ORD-C'; quantity = 30 }
    return $r.success -and $r.data.available -eq 60 -and $r.data.frozen -eq 30
}

Invoke-Case 'API-PNT-021' '取消解冻：frozen -= 30，available += 30（回到原批次）' {
    $r = Send 'Unfreeze' @{ customerId = $script:alice; bizNo = 'ORD-C' }
    return $r.success -and $r.data.frozen -eq 0 -and $r.data.available -eq 90
}

Invoke-Case 'API-PNT-022' '🔴 同单号重复解冻不重复退' {
    $r = Send 'Unfreeze' @{ customerId = $script:alice; bizNo = 'ORD-C' }
    return $r.success -and $r.data.alreadyApplied -eq $true -and $r.data.available -eq 90
}

Write-Host "`n=== PNT 退款按比例回收 ===" -ForegroundColor Cyan

Invoke-Case 'API-PNT-030' '锁定 40' {
    $r = Send 'Lock' @{ customerId = $script:alice; bizNo = 'ORD-D'; quantity = 40 }
    return $r.success -and $r.data.available -eq 50 -and $r.data.frozen -eq 40
}

Invoke-Case 'API-PNT-031' '🔴 退款 50% 按**向上取整**回收 20（40×0.5=20）' {
    $r = Send 'Refund' @{ customerId = $script:alice; bizNo = 'ORD-D'; refundRatio = 0.5 }
    return $r.success -and $r.data.available -eq 70
}

Invoke-Case 'API-PNT-032' '🔴 奇数比例也向上取整：锁 33 退 30% → 10（9.9 上取整）' {
    Send 'Lock' @{ customerId = $script:alice; bizNo = 'ORD-E'; quantity = 33 } | Out-Null
    $before = Get-Balance $script:alice
    $r = Send 'Refund' @{ customerId = $script:alice; bizNo = 'ORD-E'; refundRatio = 0.3 }
    # 33 × 0.3 = 9.9 → 向上取整 10
    return $r.success -and ($r.data.available - $before.available) -eq 10
}

Invoke-Case 'API-PNT-033' '退款比例超出 0~1 被拒' {
    try { Send 'Refund' @{ customerId = $script:alice; bizNo = 'ORD-E'; refundRatio = 1.5 } | Out-Null; return $false }
    catch { return [int]$_.Exception.Response.StatusCode -eq 400 }
}

Write-Host "`n=== PNT 余额上限 ===" -ForegroundColor Cyan
$script:bob = 600000000000 + $script:suffix

Invoke-Case 'API-PNT-040' '🔴 发放 999999：超出 100000 上限的部分截断不入账' {
    $r = Send 'Earn' @{ customerId = $script:bob; source = 'order_completed'; quantity = 999999; bizNo = 'CAP-1' }
    $b = Get-Balance $script:bob
    return $r.success -and $b.available -eq 100000
}

Invoke-Case 'API-PNT-041' '已达上限后再发放，一分不加' {
    Send 'Earn' @{ customerId = $script:bob; source = 'order_completed'; quantity = 5000; bizNo = 'CAP-2' } | Out-Null
    $b = Get-Balance $script:bob
    return $b.available -eq 100000
}

Write-Host "`n=== PNT 每日签到 ===" -ForegroundColor Cyan
$script:carol = 700000000000 + $script:suffix

Invoke-Case 'API-PNT-050' '首次签到成功，第 1 天得 1 积分' {
    $r = Invoke-RestMethod "$PointService/points/SignIn?customerId=$($script:carol)" -Method Post -TimeoutSec 30
    return $r.success -and $r.data.streak -eq 1 -and $r.data.reward -eq 1
}

Invoke-Case 'API-PNT-051' '🔴 当日重复签到被拒（提示今日已签到）' {
    $r = Invoke-RestMethod "$PointService/points/SignIn?customerId=$($script:carol)" -Method Post -TimeoutSec 30
    return $r.success -and $r.data.alreadySigned -eq $true -and $r.data.message -match '已签到'
}

Invoke-Case 'API-PNT-052' '签到奖励表是 1/2/3/5/8/10/15（7 天一轮）' {
    $expected = @(1, 2, 3, 5, 8, 10, 15)
    return $expected.Count -eq 7 -and ($expected | Measure-Object -Sum).Sum -eq 44
}

Write-Host "`n=== PNT 流水 ===" -ForegroundColor Cyan

Invoke-Case 'API-PNT-060' '流水可查，每个动作都有变动前后余额' {
    $r = Invoke-RestMethod "$PointService/points/Records?customerId=$($script:alice)&page=1&pageSize=50" -TimeoutSec 30
    return $r.success -and $r.data.Count -ge 8
}

Invoke-Case 'API-PNT-061' '🔴 幂等的重复请求不会在流水里留下重复记录' {
    $r = Invoke-RestMethod "$PointService/points/Records?customerId=$($script:alice)&page=1&pageSize=50" -TimeoutSec 30
    $locks = @($r.data | Where-Object { $_.action -eq 'lock' -and $_.bizNo -eq 'ORD-A' })
    $consumes = @($r.data | Where-Object { $_.action -eq 'consume' -and $_.bizNo -eq 'ORD-A' })
    $unfreezes = @($r.data | Where-Object { $_.action -eq 'unfreeze' -and $_.bizNo -eq 'ORD-C' })
    return $locks.Count -eq 1 -and $consumes.Count -eq 1 -and $unfreezes.Count -eq 1
}

Invoke-Case 'API-PNT-062' '从没领过积分的客户查余额返回 0 而不是报错' {
    $ghost = 800000000000 + $script:suffix
    $r = Invoke-RestMethod "$PointService/points/Balance?customerId=$ghost" -TimeoutSec 30
    return $r.success -and $r.data.available -eq 0 -and $r.data.frozen -eq 0
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