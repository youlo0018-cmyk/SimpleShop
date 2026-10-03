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
    [string]$Gateway = 'http://127.0.0.1:5008',
    [string]$Inventory = 'http://127.0.0.1:5062',
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

# 秒杀用例要走后台建商品 / 建分类 / 初始化库存，那几条只有经网关带令牌才能调
Add-Type -AssemblyName System.Net.Http
$adminHttp = [System.Net.Http.HttpClient]::new()
$dd = [System.Collections.Generic.Dictionary[string,string]]::new()
$dd['grant_type'] = 'password'
$dd['client_id'] = 'admin-app'
$dd['username'] = $AdminUser
$dd['password'] = $AdminPassword

$tokenResp = $adminHttp.PostAsync(
    "$Gateway/gateway/auth/token",
    [System.Net.Http.FormUrlEncodedContent]::new($dd)
).GetAwaiter().GetResult()

$tokenJson = ($tokenResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()) | ConvertFrom-Json
$script:adminHeaders = @{ Authorization = "Bearer $($tokenJson.access_token)" }
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

# 「预期会失败」的用例必须用这个：校验失败与业务失败都由全局异常中间件返回 HTTP 400，
# Invoke-RestMethod 见到 400 就抛异常，body 里的 { success:false, message } 拿不到，
# 用例只能看到一句 "400 (Bad Request)"，分不清是被哪条规则拒的。
function Post-Api([string]$Path, $Body) {
    try {
        return Invoke-RestMethod -Uri "$Marketing$Path" -Method Post `
            -Body ($Body | ConvertTo-Json -Depth 6) -ContentType 'application/json' -TimeoutSec 30
    } catch {
        $raw = $_.ErrorDetails.Message
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return [pscustomobject]@{ success = $false; code = -1; message = $_.Exception.Message; data = $null }
        }
        # -AsHashtable：errors 里可能出现**空字符串键**（FluentValidation 从方法调用表达式里
        # 提不出属性名时就会给空键），普通 ConvertFrom-Json 遇到空键直接抛异常。
        try { return $raw | ConvertFrom-Json -AsHashtable }
        catch { return [pscustomobject]@{ success = $false; code = -1; message = $raw; data = $null } }
    }
}

# 把 errors 里所有字段级错误文案拼成一段。
# 必须同时兼容两种形状：Post-Api 用 ConvertFrom-Json -AsHashtable 返回 Hashtable，
# 直接用 Invoke-RestMethod 的则返回 PSCustomObject。写死一种就在另一种上拿到空串，
# 断言随之静默失败——那种失败比用例红更糟，因为它看起来「通过」了。
function Get-ErrorText($resp) {
    if ($null -eq $resp.errors) { return '' }
    $errs = $resp.errors
    if ($errs -is [System.Collections.IDictionary]) {
        return (@($errs.Values | ForEach-Object { $_ }) -join ' ')
    }
    return (@($errs.PSObject.Properties | ForEach-Object { $_.Value }) -join ' ')
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

Write-Host "`n=== MKT 营销活动（满减 / 满折 / 满赠）与到手价 ===" -ForegroundColor Cyan

$script:promoIds = @()

function New-Activity([hashtable]$over) {
    $b = @{
        activityName = "活动$($script:suffix)"; activityType = 1
        thresholdAmount = 0; discountAmount = 0; discountRate = 0; giftTemplateId = 0
        targetType = 1; targets = '[]'
        startTime = $script:now.AddDays(-1).ToString('o')
        endTime = $script:now.AddDays(1).ToString('o')
        perOrderLimit = 0; totalQuantity = 0; sortOrder = 0; status = 1
        platformId = $script:suffix          # 用 suffix 当平台号，天然隔离，不影响别的用例
        merchantId = 0
    }
    foreach ($k in $over.Keys) { $b[$k] = $over[$k] }

    $r = Post '/marketing/activities/Create' $b
    if ($r.success) { $script:promoIds += [long]$r.data }
    return $r
}

function Stop-Activity([long]$id) {
    if ($id -gt 0) { Post '/marketing/activities/SetStatus' @{ activityId = $id; status = 2 } | Out-Null }
}

function FinalPrice([long]$customerId, $lines) {
    return Post '/marketing/activities/FinalPrice' @{
        customerId = $customerId; lines = $lines; sessionId = 0; platformId = $script:suffix
    }
}

# 三行各 50，合计 150
$threeLines = @(
    [pscustomobject]@{ spuId = 100; skuId = 1001; amount = 50 },
    [pscustomobject]@{ spuId = 100; skuId = 1002; amount = 50 },
    [pscustomobject]@{ spuId = 100; skuId = 1003; amount = 50 }
)

$script:mainPromoId = 0

Invoke-Case 'API-MKT-050' '新建满减活动（满 100 减 20）' {
    $r = New-Activity @{ activityType = 1; thresholdAmount = 100; discountAmount = 20; activityName = "满100减20$($script:suffix)" }
    $script:mainPromoId = [long]$r.data
    return $r.success -and $script:mainPromoId -gt 0
}

Invoke-Case 'API-MKT-051' '🔴 P0 整单优惠额按行分摊，各行之和恰好等于整单优惠' {
    $r = FinalPrice 0 $threeLines
    if (-not $r.success) { return $false }

    # 「每行各减 20」会把优惠额变成 60 元，直接把商家的钱减穿。
    # 正确是 20.00 按 50:50:50 分摊，余数给金额最大的那行。
    $sum = ($r.data.lines | Measure-Object -Property activityDiscount -Sum).Sum
    return $r.data.originalTotal -eq 150 `
        -and $r.data.activityDiscount -eq 20 `
        -and $sum -eq 20 `
        -and $r.data.finalPrice -eq 130
}

Invoke-Case 'API-MKT-052' '优惠来源角标带出活动名（商品卡要显示「满减」）' {
    $r = FinalPrice 0 $threeLines
    $first = @($r.data.lines)[0]
    return $first.source -eq 'activity' -and $first.sourceName -match '满100减20'
}

Invoke-Case 'API-MKT-053' '🔴 游客只算活动不计券' {
    $r = FinalPrice 0 $threeLines
    return $r.data.isVisitor -eq $true -and $r.data.couponDiscount -eq 0 -and $r.data.couponId -eq 0
}

Invoke-Case 'API-MKT-053b' '首个活动用完立刻停掉，避免压过后面每个用例' {
    # 不停的话它是「全场满 100 减 20」，后面每条用例都在跟它比，
    # 「多活动取力度最大」「指定 SKU 范围」这些就都测不到真实逻辑了
    Stop-Activity $script:mainPromoId
    $r = FinalPrice 0 $threeLines
    return $r.data.activityDiscount -eq 0
}

Invoke-Case 'API-MKT-054' '未达门槛不享受活动，按原价' {
    $r = FinalPrice 0 @([pscustomobject]@{ spuId = 100; skuId = 1001; amount = 30 })
    return $r.data.activityDiscount -eq 0 -and $r.data.finalPrice -eq 30 -and $r.data.usedActivity -eq $false
}

Invoke-Case 'API-MKT-055' '满折：8.5 折按 15% 算优惠' {
    $id = (New-Activity @{ activityType = 2; thresholdAmount = 100; discountRate = 8.5; activityName = "满100打85折$($script:suffix)" }).data
    if (-not $id) { return $false }

    $r = FinalPrice 0 @([pscustomobject]@{ spuId = 100; skuId = 1001; amount = 100 })
    Stop-Activity ([long]$id)
    return $r.data.activityDiscount -eq 15 -and $r.data.finalPrice -eq 85
}

Invoke-Case 'API-MKT-056' '🔴 满赠：折扣额记 0 但角标显示「赠」' {
    $id = (New-Activity @{ activityType = 3; thresholdAmount = 50; giftTemplateId = $script:templateId; activityName = "满50赠券$($script:suffix)" }).data
    if (-not $id) { return $false }

    $r = FinalPrice 0 @([pscustomobject]@{ spuId = 100; skuId = 1001; amount = 100 })
    Stop-Activity ([long]$id)
    # 满赠不是折扣：金额一分不少，但前端要显示「满 50 赠券」而不是「无优惠」
    return $r.data.activityDiscount -eq 0 -and $r.data.finalPrice -eq 100 -and @($r.data.lines)[0].source -eq 'gift'
}

Invoke-Case 'API-MKT-057' '🔴 多活动冲突取优惠力度最大的' {
    $weak = (New-Activity @{ activityType = 1; thresholdAmount = 50; discountAmount = 5; activityName = "弱$($script:suffix)" }).data
    $strong = (New-Activity @{ activityType = 1; thresholdAmount = 80; discountAmount = 20; activityName = "强$($script:suffix)" }).data
    if (-not $weak -or -not $strong) { return $false }

    $r = FinalPrice 0 @([pscustomobject]@{ spuId = 100; skuId = 1001; amount = 100 })
    Stop-Activity ([long]$weak); Stop-Activity ([long]$strong)
    return $r.data.activityDiscount -eq 20 -and @($r.data.lines)[0].sourceName -match '^强'
}

Invoke-Case 'API-MKT-058' '指定 SKU 范围：范围外的行不享受活动' {
    $id = (New-Activity @{
        activityType = 1; thresholdAmount = 0; discountAmount = 10
        targetType = 3; targets = '[1001]'
        activityName = "指定SKU$($script:suffix)"
    }).data
    if (-not $id) { return $false }

    $r = FinalPrice 0 @(
        [pscustomobject]@{ spuId = 100; skuId = 1001; amount = 100 },
        [pscustomobject]@{ spuId = 200; skuId = 2001; amount = 100 }
    )
    Stop-Activity ([long]$id)
    # 只有 1001 这一行减 10，2001 不减
    return $r.data.activityDiscount -eq 10 -and @($r.data.lines)[1].activityDiscount -eq 0
}

Invoke-Case 'API-MKT-059' '🔴 单行封底 0.01：优惠不能把某一行打成 0 元' {
    $id = (New-Activity @{ activityType = 1; thresholdAmount = 0; discountAmount = 100; activityName = "打穿$($script:suffix)" }).data
    if (-not $id) { return $false }

    $r = FinalPrice 0 @([pscustomobject]@{ spuId = 100; skuId = 1001; amount = 100 })
    Stop-Activity ([long]$id)
    return $r.data.finalPrice -eq 0.01
}

Invoke-Case 'API-MKT-060' '🔴 满减没填金额被拒（否则活动命中却不打折）' {
    $r = Post-Api '/marketing/activities/Create' @{
        activityName = "空金额$($script:suffix)"; activityType = 1
        thresholdAmount = 50; discountAmount = 0
        startTime = $script:now.AddDays(-1).ToString('o')
        endTime = $script:now.AddDays(1).ToString('o')
        platformId = $script:suffix
    }
    # 顶层 message 是统一的「请求参数校验失败」，具体原因在 errors 里
    # （CODING_STANDARD 3.4：前端只用 errors 做 tip 提示，不飘红输入框）
    return (-not $r.success) -and (Get-ErrorText $r) -match '优惠金额'
}

Invoke-Case 'API-MKT-061' '结束时间早于开始时间被拒' {
    $r = Post-Api '/marketing/activities/Create' @{
        activityName = "时间倒置$($script:suffix)"; activityType = 1
        thresholdAmount = 0; discountAmount = 5
        startTime = $script:now.ToString('o')
        endTime = $script:now.AddDays(-1).ToString('o')
        platformId = $script:suffix
    }
    return (-not $r.success) -and (Get-ErrorText $r) -match '晚于开始时间'
}

Invoke-Case 'API-MKT-062' '🔴 时间已过的活动不参与计算（时间窗按 now 过滤，不缓存）' {
    $id = (New-Activity @{
        activityType = 1; thresholdAmount = 0; discountAmount = 30
        startTime = $script:now.AddDays(-10).ToString('o')
        endTime = $script:now.AddDays(-5).ToString('o')
        activityName = "过期$($script:suffix)"
    }).data
    if (-not $id) { return $false }

    $r = FinalPrice 0 @([pscustomobject]@{ spuId = 100; skuId = 1001; amount = 100 })
    Stop-Activity ([long]$id)
    return $r.data.activityDiscount -eq 0
}

Invoke-Case 'API-MKT-063' '后台列表带出类型 / 范围 / 状态中文名（不显示数字枚举）' {
    $r = Post '/marketing/activities/List' @{ platformId = 0; page = 1; pageSize = 20 }
    $first = @($r.data.items)[0]
    return $r.success -and $first.typeName -ne '' -and $first.targetName -ne '' -and $first.statusName -ne ''
}

Invoke-Case 'API-MKT-064' '🔴 删除后不再参与计算（软删）' {
    $id = (New-Activity @{ activityType = 1; thresholdAmount = 0; discountAmount = 40; activityName = "待删$($script:suffix)" }).data
    if (-not $id) { return $false }

    $before = FinalPrice 0 @([pscustomobject]@{ spuId = 100; skuId = 1001; amount = 100 })
    Post '/marketing/activities/Delete' @{ activityId = [long]$id } | Out-Null
    $after = FinalPrice 0 @([pscustomobject]@{ spuId = 100; skuId = 1001; amount = 100 })

    return $before.data.activityDiscount -eq 40 -and $after.data.activityDiscount -eq 0
}

Invoke-Case 'API-MKT-065' '清理本节所有活动' {
    foreach ($id in $script:promoIds) { Post '/marketing/activities/Delete' @{ activityId = [long]$id } | Out-Null }
    $script:promoIds = @()
    return $true
}

Write-Host "`n=== SKL 秒杀：库存划出与回补（S-1）===" -ForegroundColor Cyan

# 秒杀要用一个**真实存在且有库存**的 SKU：发布时真的会去库存服务划库存，
# 拿个不存在的 SKU 去测只能测到「划出失败」这一条分支。
$script:sklProductId = 0
$script:sklCategoryIds = @()
$script:sklSkuId = 0
$script:sklOriginPrice = 200
$script:sklInitStock = 50
$script:sklSessionId = 0
$script:sklItemId = 0
$script:sklQty = 10

function Get-SkuStock([long]$SkuId) {
    $r = Invoke-RestMethod "$Inventory/internal/inventory/Snapshot?skuIds=$SkuId" -TimeoutSec 20
    return @($r.data | Where-Object { $_.skuId -eq $SkuId })[0]
}

function New-SklSession([int]$Hours = 1) {
    $now = [DateTime]::UtcNow
    return (Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Create' -Method Post `
        -Body (@{
            sessionName = "秒杀场次$($script:suffix)"; platformId = 0; merchantId = 0
            startTime = $now.AddHours(-1).ToString('o'); endTime = $now.AddHours($Hours).ToString('o')
            sortOrder = 0
        } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 20).data
}

function Publish-Skl([long]$Id, [bool]$Force = $false) {
    return Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Publish' -Method Post `
        -Body (@{ sessionId = $Id; force = $Force } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
}

Invoke-Case 'API-SKL-000' '准备：一个有 50 件库存、单价 200 的 SKU' {
    $a = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = 0; categoryName = "秒杀$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $b = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = $a; categoryName = "秒杀$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $c = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = $b; categoryName = "秒杀$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $script:sklCategoryIds = @($a, $b, $c)

    $body = @{
        productId = 0; spuName = "秒杀商品$($script:suffix)"; categoryId = $c
        deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
        specs = @(@{ specName = '颜色'; specValues = @('红') })
        skus = @(@{ skuCode = "SKL$($script:suffix)"; specValues = @('红'); price = $script:sklOriginPrice; stock = $script:sklInitStock; status = 1 })
    }
    $script:sklProductId = [long](Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:adminHeaders `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30).data

    $det = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:sklProductId)" -Headers $script:adminHeaders -TimeoutSec 30
    $script:sklSkuId = [long]$det.data.skus[0].id

    return $script:sklSkuId -gt 0 -and (Get-SkuStock $script:sklSkuId).available -eq $script:sklInitStock
}

Invoke-Case 'API-SKL-001' '建场次：未开始、库存未划出' {
    $script:sklSessionId = [long](New-SklSession)
    $list = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/List' -Method Post `
        -Body (@{ status = 0; page = 1; pageSize = 50 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    $row = @($list.data.items | Where-Object { $_.sessionId -eq "$($script:sklSessionId)" })[0]
    return $script:sklSessionId -gt 0 -and $row.status -eq 10 -and $row.statusName -eq '未开始' -and $row.stockTransferred -eq $false
}

Invoke-Case 'API-SKL-002' '🔴 秒杀价不低于原价被拒（「秒杀」比原价还贵没有意义）' {
    $r = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/Add' -Method Post `
        -Body (@{ sessionId = $script:sklSessionId; skuId = $script:sklSkuId; seckillPrice = $script:sklOriginPrice; seckillStock = 5; perUserLimit = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    return (-not $r.success) -and $r.message -match '低于商品原价'
}

Invoke-Case 'API-SKL-003' '加商品成功，快照下商品名 / 规格 / 图 / 原价' {
    $script:sklItemId = [long](Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/Add' -Method Post `
        -Body (@{ sessionId = $script:sklSessionId; skuId = $script:sklSkuId; seckillPrice = 99.00; seckillStock = $script:sklQty; perUserLimit = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20).data

    $list = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/List' -Method Post `
        -Body (@{ sessionId = $script:sklSessionId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    $row = @($list.data | Where-Object { $_.itemId -eq "$($script:sklItemId)" })[0]
    return $script:sklItemId -gt 0 -and $row.skuId -eq "$($script:sklSkuId)" `
        -and $row.seckillStock -eq $script:sklQty -and $row.remaining -eq $script:sklQty -and $row.soldCount -eq 0
}

Invoke-Case 'API-SKL-010' '🔴 P0 发布：库存从常规池划到秒杀池（50 → 40）' {
    $before = (Get-SkuStock $script:sklSkuId).available
    $r = Publish-Skl $script:sklSessionId
    $after = (Get-SkuStock $script:sklSkuId).available

    Write-Host ("        {0} 库存 {1} → {2}" -f $r.message, $before, $after) -ForegroundColor DarkGray
    return $r.success -and $r.data.reservedTotal -eq $script:sklQty `
        -and $before - $after -eq $script:sklQty
}

Invoke-Case 'API-SKL-011' '🔴 P0 重复发布被拒，库存**不能被划第二遍**' {
    # 这是整个库存方案最贵的一条防线：重复点发布会把常规库存扣走第二份，
    # 而秒杀池子里只有一份货——等于凭空蒸发一批库存
    $before = (Get-SkuStock $script:sklSkuId).available
    $r = Publish-Skl $script:sklSessionId
    $after = (Get-SkuStock $script:sklSkuId).available

    return (-not $r.success) -and $r.message -match '不能重复发布' -and $before -eq $after
}

Invoke-Case 'API-SKL-012' '发布后场次变为「进行中」且标记库存已划出' {
    $list = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/List' -Method Post `
        -Body (@{ status = 0; page = 1; pageSize = 50 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    $row = @($list.data.items | Where-Object { $_.sessionId -eq "$($script:sklSessionId)" })[0]
    return $row.status -eq 20 -and $row.statusName -eq '进行中' -and $row.stockTransferred -eq $true
}

Invoke-Case 'API-SKL-013' '🔴 发布后不能再加商品（库存已按当时的列表一次性划走）' {
    $r = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/Add' -Method Post `
        -Body (@{ sessionId = $script:sklSessionId; skuId = $script:sklSkuId; seckillPrice = 88.00; seckillStock = 1; perUserLimit = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    # 事后再加的商品没有对应的划转记录，用户会看到一个抢不了也退不掉的商品
    return (-not $r.success) -and $r.message -match '不能'
}

Invoke-Case 'API-SKL-014' '🔴 P0 中止场次：剩余库存立即回补常规池（40 → 50）' {
    $before = (Get-SkuStock $script:sklSkuId).available
    $r = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Finish' -Method Post `
        -Body (@{ sessionId = $script:sklSessionId; cancel = $true } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    $after = (Get-SkuStock $script:sklSkuId).available

    Write-Host ("        {0} 库存 {1} → {2}" -f $r.message, $before, $after) -ForegroundColor DarkGray
    return $r.success -and $r.data.status -eq 40 -and $r.data.releasedTotal -eq $script:sklQty `
        -and $after - $before -eq $script:sklQty
}

Invoke-Case 'API-SKL-015' '🔴 重复中止被拒且不再回补第二遍' {
    $before = (Get-SkuStock $script:sklSkuId).available
    $r = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Finish' -Method Post `
        -Body (@{ sessionId = $script:sklSessionId; cancel = $true } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    $after = (Get-SkuStock $script:sklSkuId).available
    return $r.success -and $before -eq $after
}

Invoke-Case 'API-SKL-016' '已取消的场次不出现在前台（用户只该看到即将开场与进行中）' {
    $pub = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Public' -Method Post `
        -Body (@{ platformId = 0; sessionId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    $ids = @($pub.data | ForEach-Object { $_.sessionId })
    return -not ($ids -contains "$($script:sklSessionId)")
}

Invoke-Case 'API-SKL-017' '🔴 数量上限被校验挡住（0 件不是有效配置）' {
    # 校验失败返回 HTTP 400，Invoke-RestMethod 会直接抛异常。
    # 这里要的是「被拒绝了」这个事实，所以把异常也当成拒绝
    try {
        $r = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/Add' -Method Post `
            -Body (@{ sessionId = $script:sklSessionId; skuId = $script:sklSkuId; seckillPrice = 50.00; seckillStock = 0; perUserLimit = 1 } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 20
        return (-not $r.success)
    } catch {
        return [int]$_.Exception.Response.StatusCode -eq 400
    }
}

Invoke-Case 'API-SKL-018' '清理：删商品 → 删分类' {
    if ($script:sklProductId -gt 0) {
        Invoke-RestMethod "$Gateway/gateway/products/Delete" -Method Post -Headers $script:adminHeaders `
            -Body (@{ productId = $script:sklProductId } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
    foreach ($id in [array]($script:sklCategoryIds | Sort-Object -Descending)) {
        Invoke-RestMethod "$Gateway/gateway/categories/Delete" -Method Post -Headers $script:adminHeaders `
            -Body (@{ categoryId = $id } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
    return $true
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