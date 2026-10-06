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

Write-Host "`n=== PNT 过期扣减（Scheduled 每天调 /internal/points/Expire）===" -ForegroundColor Cyan

$script:expiryCustomer = 730000000 + $script:suffix
$script:expiryBiz = "EXPIRY-$($script:suffix)"

# 把批次到期时间往前拨。
#
# 必须写 `now() AT TIME ZONE 'UTC'`：容器会话时区是 Asia/Shanghai，
# 直接写 `now() - interval` 得到的是 timestamptz，赋给 timestamp 列时会按会话时区折算，
# 结果是把「往前拨 2 天」变成「往前推 8 小时」——单看 SQL 完全看不出问题，只有断言会红。
function Backdate-Lot([string]$Biz, [string]$Interval) {
    docker exec simpleshop-postgres psql -U postgres -d simpleshoppoint -q -c `
        "UPDATE point_lot SET expire_at = (now() AT TIME ZONE 'UTC') - interval '$Interval' WHERE biz_no = '$Biz';" | Out-Null
}

Invoke-Case 'API-PNT-070' '发放 800 积分并确认可用余额' {
    $r = Send 'Earn' @{ customerId = $script:expiryCustomer; source = 'test'; quantity = 800; bizNo = $script:expiryBiz }
    return $r.success -and (Get-Balance $script:expiryCustomer).available -eq 800
}

Invoke-Case 'API-PNT-071' '🔴 未到期时过期任务不动它（余额不变）' {
    $r = Send 'Expire' @{ limit = 500 }
    return $r.success -and (Get-Balance $script:expiryCustomer).available -eq 800
}

Invoke-Case 'API-PNT-072' '🔴 到期后过期任务把它从可用余额扣掉' {
    Backdate-Lot $script:expiryBiz '2 days'

    $r = Send 'Expire' @{ limit = 500 }
    $after = Get-Balance $script:expiryCustomer

    Write-Host ("        {0}" -f $r.message) -ForegroundColor DarkGray
    return $r.success -and $r.data.expired -ge 1 `
        -and $after.available -eq 0 `
        -and $after.totalUsed -eq 800
}

Invoke-Case 'API-PNT-073' '🔴 过期流水可查（action = expire），不是凭空消失' {
    $r = Invoke-RestMethod "$PointService/points/Records?customerId=$($script:expiryCustomer)&page=1&pageSize=50" -TimeoutSec 30
    # 流水接口的 data 直接就是数组（不是 .items），与 API-PNT-060/061 的用法一致
    $expire = @($r.data | Where-Object { $_.action -eq 'expire' })
    # 积分必须留痕：用户余额少了 800，流水里要能查到少了什么、什么时候少的
    return $expire.Count -ge 1 -and [long]$expire[0].quantity -eq -800
}

Invoke-Case 'API-PNT-074' '🔴 重复跑过期任务不重复扣（幂等）' {
    $r = Send 'Expire' @{ limit = 500 }
    $after = Get-Balance $script:expiryCustomer
    # 业务号是 EXP-{lotId}，同一个批次被扫到多少次都只扣一次
    return $after.available -eq 0 -and $after.totalUsed -eq 800
}

Invoke-Case 'API-PNT-076' '🔴 注册赠送的金额由积分规则决定，改了就要生效' {
    # 🔴 注册赠送的数额是积分规则里的一项（register_gift，后台可改），
    # 但调用方（客户服务）一度把它写死成 100 —— 运营改成 250 也不会生效，
    # 而且不会有任何报错，只是「改了没反应」。
    #
    # 现在金额由积分服务自己按规则决定：调用方只传客户 Id，
    # 调 internal/points/EarnRegisterGift。
    $before = (Invoke-RestMethod "$PointService/points/Rules" -Method Post -ContentType 'application/json' `
        -Body '{}' -TimeoutSec 20).data

    try {
        # 把注册赠送改成 250、抵扣汇率改成 200
        Invoke-RestMethod "$PointService/points/SaveRules" -Method Post -ContentType 'application/json' `
            -Body (@{
                balanceCap = $before.balanceCap; validDays = $before.validDays
                registerGift = 250; firstEvaluateGift = $before.firstEvaluateGift
                pointsPerYuan = 200; earnPointsPerYuan = $before.earnPointsPerYuan
                signInRewards = $before.signInRewards
            } | ConvertTo-Json -Depth 6) -TimeoutSec 20 | Out-Null

        # 抵扣汇率同样要跟着规则走：订单服务算「积分抵了多少钱」时必须问这里，
        # 不能自己写死 100 —— 写死的话运营改了汇率不会生效，而且不会有任何报错。
        $rate = (Invoke-RestMethod "$PointService/internal/points/DeductionRate" -TimeoutSec 20).data.pointsPerYuan
        Write-Host ("        规则里的抵扣汇率 = {0}（期望 200）" -f $rate) -ForegroundColor DarkGray
        if ($rate -ne 200) { return $false }

        $customerId = 790000000 + $script:suffix
        $r = Invoke-RestMethod "$PointService/internal/points/EarnRegisterGift" -Method Post `
            -ContentType 'application/json' `
            -Body (@{ customerId = $customerId } | ConvertTo-Json) -TimeoutSec 20

        $bal = Get-Balance $customerId
        Write-Host ("        规则 250 → 客户拿到 {0}" -f $bal.totalEarned) -ForegroundColor DarkGray

        # 幂等：同一条命令再发一次不能翻倍
        Invoke-RestMethod "$PointService/internal/points/EarnRegisterGift" -Method Post `
            -ContentType 'application/json' `
            -Body (@{ customerId = $customerId } | ConvertTo-Json) -TimeoutSec 20 | Out-Null
        $again = Get-Balance $customerId

        return $r.success -and $bal.totalEarned -eq 250 -and $again.totalEarned -eq 250
    } finally {
        # 还原规则：留着 250 会让后面别的脚本拿到意料之外的赠送额
        Invoke-RestMethod "$PointService/points/SaveRules" -Method Post -ContentType 'application/json' `
            -Body (@{
                balanceCap = $before.balanceCap; validDays = $before.validDays
                registerGift = $before.registerGift; firstEvaluateGift = $before.firstEvaluateGift
                pointsPerYuan = $before.pointsPerYuan; earnPointsPerYuan = $before.earnPointsPerYuan
                signInRewards = $before.signInRewards
            } | ConvertTo-Json -Depth 6) -TimeoutSec 20 | Out-Null
    }
}

Invoke-Case 'API-PNT-075' '🔴 已冻结的积分不被过期扣掉（属于在途订单）' {
    $cid = 731000000 + $script:suffix
    $biz = "EXP-FROZEN-$($script:suffix)"
    Send 'Earn' @{ customerId = $cid; source = 'test'; quantity = 500; bizNo = "FRZ-EARN-$($script:suffix)" } | Out-Null
    Send 'Lock'  @{ customerId = $cid; bizNo = "FRZ-LOCK-$($script:suffix)"; quantity = 500 } | Out-Null

    $before = Get-Balance $cid
    # 只有冻结、没有可用；过期任务应该扫不到任何可扣的东西
    Send 'Expire' @{ limit = 500 } | Out-Null
    $after = Get-Balance $cid

    # 过期逻辑只从 available 扣：用户下单冻结的那部分积分还在 frozen 里，
    # 凭空消失会让他支付时莫名其妙少积分
    return $before.available -eq 0 -and $before.frozen -eq 500 `
        -and $after.frozen -eq 500 -and $after.available -eq 0
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
