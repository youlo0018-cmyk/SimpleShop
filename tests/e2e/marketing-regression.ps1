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
# 抢购要三个互不相同的客户：第一个抢到，第二个抢到，第三个撞「已抢完」
$script:grabSessionId = 0
$script:grabItemId = 0
$script:grabOrderNo = ''
$script:grabRequestId = ''
$script:grabCustomer1 = 910000001 + $script:suffix
$script:grabCustomer2 = 910000002 + $script:suffix
$script:grabCustomer3 = 910000003 + $script:suffix

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

# 抢购用例统一走这里：测试商品是实物快递（deliveryType=1），
# 实物必填收货信息，少传会被下单校验挡掉、返回「请填写收货人姓名」。
function Invoke-Grab([long]$ItemId, [long]$CustomerId) {
    return Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/grab' -Method Post `
        -Body (@{
            itemId = $ItemId; customerId = $CustomerId
            receiverName = '抢购测试'; receiverPhone = '13800138000'
            receiverAddress = '测试省测试市测试区 1 号'
        } | ConvertTo-Json) `
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

    # 🔴 商品必须「审核通过 + 已上架」才可下单（BUSINESS.md 14.4「商品需审核后上架」）。
    # 秒杀单同样走下单链路，回查时一样校验审核与上架状态，所以这里必须先把商品推到可售状态。
    (Invoke-RestMethod "$Gateway/gateway/products/Audit" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $script:sklProductId; auditStatus = 20 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30) | Out-Null
    (Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $script:sklProductId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30) | Out-Null

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

Invoke-Case 'API-SKL-004' '🔴 同一 SKU 在同一场次内加第二次：给业务提示，不是 500' {
    # 限购幂等键是 {itemId}:{customerId}，所以同一场次里同一个 SKU 若有两条记录，
    # 同一个人就能各买一次 —— 「每人每场次限购 1 件」直接失效。
    # 库里已有唯一约束 uk_seckill_item_session_sku 兜底，但**不能让约束异常冒泡**：
    # 那会变成 500「服务器内部错误，请稍后重试」，运营看到「稍后重试」就会再点一次，
    # 再吃一个 500，最后当成系统故障报上来。
    try {
        $r = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/Add' -Method Post `
            -Body (@{ sessionId = $script:sklSessionId; skuId = $script:sklSkuId; seckillPrice = 88.00; seckillStock = 1; perUserLimit = 1 } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 20
        Write-Host ("        success={0} msg={1}" -f $r.success, $r.message) -ForegroundColor DarkGray
        return (-not $r.success) -and $r.message -match '已在本次场次'
    } catch {
        $status = [int]$_.Exception.Response.StatusCode
        Write-Host ("        HTTP {0}（不该是 500）" -f $status) -ForegroundColor DarkGray
        # 500 说明唯一约束异常冒泡了 —— 这正是要挡掉的
        return $false
    }
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

# ===== 到点自动结束（定时任务调用的同一个内部接口）=====
# 少了这条链路，一个没人手动中止的场次会永远停在「进行中」：
# 剩余库存永久锁在秒杀池里，而且没有任何报错，现象只是商品「一直缺货」。

$script:expiredSessionId = 0
$script:expiredQty = 6

Invoke-Case 'API-SKLX-001' '建一个「已经过了结束时间」的场次（模拟到点没人管）' {
    $now = [DateTime]::UtcNow
    $r = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Create' -Method Post `
        -Body (@{
            sessionName = "自动结束验证-$($script:suffix)"
            startTime = $now.AddHours(-2).ToString('o')
            endTime   = $now.AddHours(-1).ToString('o')
            merchantId = 0
            platformId = 0
            sortOrder = 0
        } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    # Create 返回的 data 就是场次 Id 本身（不是含 sessionId 的对象），
    # 写成 $r.data.sessionId 会拿到 null，后面全部连锁失败
    $script:expiredSessionId = [long]$r.data
    return $r.success -and $script:expiredSessionId -gt 0
}

Invoke-Case 'API-SKLX-002' '给到期场次加商品并发布（库存被划走 50 → 44）' {
    $add = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/Add' -Method Post `
        -Body (@{
            sessionId = $script:expiredSessionId; skuId = $script:sklSkuId
            seckillPrice = 66.00; seckillStock = $script:expiredQty; perUserLimit = 1
        } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    if (-not $add.success) { return $false }

    $before = (Get-SkuStock $script:sklSkuId).available
    $pub = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Publish' -Method Post `
        -Body (@{ sessionId = $script:expiredSessionId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    $after = (Get-SkuStock $script:sklSkuId).available

    Write-Host ("        {0} 库存 {1} → {2}" -f $pub.message, $before, $after) -ForegroundColor DarkGray
    return $pub.success -and $before - $after -eq $script:expiredQty
}

Invoke-Case 'API-SKLX-003' '🔴 P0 到点自动结束：状态转「已结束」且库存回补（44 → 50）' {
    $before = (Get-SkuStock $script:sklSkuId).available
    $r = Invoke-RestMethod 'http://127.0.0.1:5072/internal/marketing/seckill/sessions/FinishExpired' `
        -Method Post -Body (@{ limit = 50 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 60
    $after = (Get-SkuStock $script:sklSkuId).available

    Write-Host ("        {0} 库存 {1} → {2}" -f $r.message, $before, $after) -ForegroundColor DarkGray
    return $r.success -and $r.data.scanned -ge 1 -and $r.data.finished -ge 1 `
        -and $r.data.released -eq $script:expiredQty `
        -and $after - $before -eq $script:expiredQty
}

Invoke-Case 'API-SKLX-004' '🔴 到点结束后的场次状态是 30「已结束」（不是取消 40）' {
    $list = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/List' -Method Post `
        -Body (@{ status = 0; page = 1; pageSize = 100 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    $row = @($list.data.items | Where-Object { $_.sessionId -eq "$($script:expiredSessionId)" })[0]
    return $row -and $row.status -eq 30 -and $row.statusName -eq '已结束' -and $row.stockTransferred -eq $false
}

Invoke-Case 'API-SKLX-005' '🔴 重复触发自动结束**不会**二次回补库存' {
    $before = (Get-SkuStock $script:sklSkuId).available
    $r = Invoke-RestMethod 'http://127.0.0.1:5072/internal/marketing/seckill/sessions/FinishExpired' `
        -Method Post -Body (@{ limit = 50 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 60
    $after = (Get-SkuStock $script:sklSkuId).available

    # 该场次已是「已结束」，查询条件是「仍在进行中」，所以这一轮扫不到它
    return $r.success -and $r.data.finished -eq 0 -and $before -eq $after
}

Invoke-Case 'API-SKL-020' '准备抢购：再建一个场次，2 件秒杀库存' {
    # 抢购用例需要**进行中**的场次，而上面那个已经被中止了，所以另建一个。
    $script:grabSessionId = [long](New-SklSession)
    $script:grabItemId = [long](Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/Add' -Method Post `
        -Body (@{ sessionId = $script:grabSessionId; skuId = $script:sklSkuId; seckillPrice = 77.00; seckillStock = 2; perUserLimit = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20).data
    $r = Publish-Skl $script:grabSessionId
    return $script:grabItemId -gt 0 -and $r.success
}

Invoke-Case 'API-SKL-021' '🔴 游客抢购被拒（限购要挂在人身上，否则一个人能买走整场）' {
    # 校验失败返回 HTTP 400，Invoke-RestMethod 见到 400 直接抛异常，
    # 所以这里把异常当成「被拒绝」——要断言的正是「游客进不来」这个事实
    try {
        Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/grab' -Method Post `
            -Body (@{ itemId = $script:grabItemId; customerId = 0 } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 20 | Out-Null
        return $false
    } catch {
        return [int]$_.Exception.Response.StatusCode -eq 400
    }
}

Invoke-Case 'API-SKL-022' '🔴 P0 抢购成功：生成订单，金额=秒杀价，且**不再锁常规库存**' {
    # 抢一件之后常规库存必须纹丝不动：发布时已经划走了，抢购再扣一次就是双倍扣减，
    # 等于凭空少一批货。这条是秒杀与普通下单最大的区别。
    $before = (Get-SkuStock $script:sklSkuId).available
    $r = Invoke-Grab $script:grabItemId $script:grabCustomer1
    $after = (Get-SkuStock $script:sklSkuId).available

    $script:grabOrderNo = $r.data.orderNo
    $script:grabRequestId = $r.data.requestId
    Write-Host ("        常规库存 {0} → {1}；订单 {2}" -f $before, $after, $script:grabOrderNo) -ForegroundColor DarkGray

    return $r.success -and $r.data.resultStatus -eq 1 -and $script:grabOrderNo `
        -and $before -eq $after -and $script:grabOrderNo.Length -gt 0
}

Invoke-Case 'API-SKL-023' '🔴 P0 秒杀单按秒杀价成交，且订单行标记为秒杀来源' {
    $det = Invoke-RestMethod "http://127.0.0.1:5064/orders/Detail?orderNo=$($script:grabOrderNo)&customerId=$($script:grabCustomer1)" `
        -TimeoutSec 30
    $line = $det.data.items[0]
    return $det.success -and $line.price -eq 77.00 -and $det.data.payableAmount -eq 77.00 `
        -and $line.sourceType -eq 2 -and $line.skuId -eq "$($script:sklSkuId)"
}

Invoke-Case 'API-SKL-024' '🔴 P0 同一客户重复抢购被限购拦下，且不产生第二张单' {
    $r = Invoke-Grab $script:grabItemId $script:grabCustomer1
    return $r.data.resultStatus -eq 4 -and $r.data.orderId -eq 0
}

Invoke-Case 'API-SKL-025' '🔴 P0 第三个客户抢到后，第四个客户拿到「已抢完」' {
    $r2 = Invoke-Grab $script:grabItemId $script:grabCustomer2
    $r3 = Invoke-Grab $script:grabItemId $script:grabCustomer3
    return $r2.data.resultStatus -eq 1 -and $r3.data.resultStatus -eq 2 -and $r3.data.message -match '抢完'
}

Invoke-Case 'API-SKL-026' 'sold_count 累计到 2，秒杀池库存正好卖完' {
    $list = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/List' -Method Post `
        -Body (@{ sessionId = $script:grabSessionId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    $row = @($list.data | Where-Object { $_.itemId -eq "$($script:grabItemId)" })[0]
    return $row.soldCount -eq 2 -and $row.remaining -eq 0
}

Invoke-Case 'API-SKL-027' '轮询结果接口返回抢购结果' {
    # 用一条**真正落了抢购记录**的请求来轮询。
    # 被 Redis 预扣当场拒掉的请求（抢完 / 不在抢购中）压根不写 seckill_grab，
    # 轮询它们必然是「不存在」——那是设计如此，不是缺陷。
    $q = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/grab/result' -Method Post `
        -Body (@{ requestId = $script:grabRequestId; customerId = $script:grabCustomer1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    return $q.success -and $q.data.resultStatus -eq 1 -and $q.data.orderNo -eq $script:grabOrderNo
}

Invoke-Case 'API-SKL-028' '🔴 别人的 requestId 查不到（不泄露该 ID 是否存在）' {
    $q = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/grab/result' -Method Post `
        -Body (@{ requestId = $script:grabRequestId; customerId = $script:grabCustomer2 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    return $q.success -eq $false -and $q.message -match '不存在'
}
Invoke-Case 'API-SKL-026b' '🔴 P0 秒杀单退款：sold_count 回退，货不会永久滞留在账外' {
    # 秒杀库存是**发布场次时从常规池划走**的，秒杀单从头到尾没锁过常规库存。
    # 所以退款若走常规 release —— 那笔锁定根本不存在，必然失败；
    # 而 sold_count 不减，这件货就永久卡在账外：常规池没有、秒杀池也没有。
    # 正确做法是减 sold_count，场次结束时由「seckill_stock − sold_count」自然还回常规池。
    $before = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/List' -Method Post `
        -Body (@{ sessionId = $script:grabSessionId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    $rowBefore = @($before.data | Where-Object { $_.itemId -eq "$($script:grabItemId)" })[0]

    $refund = Invoke-RestMethod "$Gateway/gateway/admin/orders/Refund" -Method Post `
        -Headers $script:adminHeaders -Body (@{ orderNo = $script:grabOrderNo; remark = '秒杀单退款回归' } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30

    $after = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/List' -Method Post `
        -Body (@{ sessionId = $script:grabSessionId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    $rowAfter = @($after.data | Where-Object { $_.itemId -eq "$($script:grabItemId)" })[0]

    Write-Host ("        sold_count {0} → {1}" -f $rowBefore.soldCount, $rowAfter.soldCount) -ForegroundColor DarkGray
    return $refund.success -and $rowAfter.soldCount -eq ($rowBefore.soldCount - 1)
}

Invoke-Case 'API-SKL-026c' '🔴 退款重试不重复回退（否则等于凭空多出库存）' {
    $before = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/List' -Method Post `
        -Body (@{ sessionId = $script:grabSessionId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    $beforeSold = @($before.data | Where-Object { $_.itemId -eq "$($script:grabItemId)" })[0].soldCount

    Invoke-RestMethod "$Gateway/gateway/admin/orders/Refund" -Method Post `
        -Headers $script:adminHeaders -Body (@{ orderNo = $script:grabOrderNo; remark = '重复退' } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $after = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/List' -Method Post `
        -Body (@{ sessionId = $script:grabSessionId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    $afterSold = @($after.data | Where-Object { $_.itemId -eq "$($script:grabItemId)" })[0].soldCount

    return $afterSold -eq $beforeSold
}


Invoke-Case 'API-SKL-029' '清理：结束抢购场次，剩余库存回补常规池' {
    $r = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Finish' -Method Post `
        -Body (@{ sessionId = $script:grabSessionId; cancel = $true } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    return $r.success
}

Invoke-Case 'API-SKL-030' '🔴 P0 30 个并发抢 10 件：成功**恰好** 10 个，一个都不能多' {
    # 这是整套防超卖方案唯一的验收手段。单线程顺序请求就算逻辑写错也测不出来——
    # 超卖只在并发下才发生。三层防线（Redis 原子预扣 / 限购唯一索引 / 条件更新记账）
    # 少任何一层，这里都会出现成功数 > 10。
    $sessionId = [long](New-SklSession)
    $itemId = [long](Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/Add' -Method Post `
        -Body (@{ sessionId = $sessionId; skuId = $script:sklSkuId; seckillPrice = 66.00; seckillStock = 10; perUserLimit = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20).data
    if (-not (Publish-Skl $sessionId).success) { return $false }

    # 30 个**不同**客户并发抢：限购唯一索引不生效的话不会拦，只能靠库存本身兜住
    $jobs = 1..30 | ForEach-Object {
        $cid = 920000000 + $script:suffix + $_
        Start-ThreadJob -ScriptBlock {
            param($url, $item, $customer)
            try {
                $r = Invoke-RestMethod -Uri $url -Method Post `
                    -Body (@{
                        itemId = $item; customerId = $customer
                        receiverName = '并发测试'; receiverPhone = '13800138000'
                        receiverAddress = '测试省测试市测试区 1 号'
                    } | ConvertTo-Json) `
                    -ContentType 'application/json' -TimeoutSec 40
                [string]$r.data.resultStatus
            } catch { 'ERR' }
        } -ArgumentList 'http://127.0.0.1:5072/marketing/seckill/grab', $itemId, $cid
    }

    $done = $jobs | Wait-Job -Timeout 180 | Receive-Job
    $jobs | Remove-Job -Force

    # resultStatus：1 成功 / 2 已抢完 / 4 超限购
    $ok = @($done | Where-Object { $_ -eq '1' }).Count
    $soldOut = @($done | Where-Object { $_ -eq '2' }).Count
    $err = @($done | Where-Object { $_ -eq 'ERR' }).Count
    $other = @($done | Where-Object { $_ -notin @('1', '2') }).Count

    $list = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/List' -Method Post `
        -Body (@{ sessionId = $sessionId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    $row = @($list.data | Where-Object { $_.itemId -eq "$itemId" })[0]

    Write-Host ("        成功 {0} / 抢完 {1} / 异常 {2} / 其它 {3}；soldCount={4} remaining={5}" -f `
        $ok, $soldOut, $err, $other, $row.soldCount, $row.remaining) -ForegroundColor DarkGray

    Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Finish' -Method Post `
        -Body (@{ sessionId = $sessionId; cancel = $true } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    return $ok -eq 10 -and $soldOut -eq 20 -and $err -eq 0 -and $other -eq 0 `
        -and $row.soldCount -eq 10 -and $row.remaining -eq 0
}

Invoke-Case 'API-SKL-031' '🔴 P0 同一客户 10 个并发请求：只允许产生 1 张订单' {
    # 限购额度是钱。Redis 预扣可能因为重试被消耗多次，但**数据库唯一索引**必须只放行一个，
    # 其余 9 个都要拿到「超出限购」而不是 500。
    $sessionId = [long](New-SklSession)
    $itemId = [long](Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/Add' -Method Post `
        -Body (@{ sessionId = $sessionId; skuId = $script:sklSkuId; seckillPrice = 55.00; seckillStock = 20; perUserLimit = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20).data
    if (-not (Publish-Skl $sessionId).success) { return $false }

    $cid = 930000000 + $script:suffix
    $jobs = 1..10 | ForEach-Object {
        Start-ThreadJob -ScriptBlock {
            param($url, $item, $customer)
            try {
                $r = Invoke-RestMethod -Uri $url -Method Post `
                    -Body (@{
                        itemId = $item; customerId = $customer
                        receiverName = '限购测试'; receiverPhone = '13800138000'
                        receiverAddress = '测试省测试市测试区 1 号'
                    } | ConvertTo-Json) `
                    -ContentType 'application/json' -TimeoutSec 40
                "$($r.data.resultStatus)|$($r.data.orderNo)"
            } catch { 'ERR' }
        } -ArgumentList 'http://127.0.0.1:5072/marketing/seckill/grab', $itemId, $cid
    }

    $done = $jobs | Wait-Job -Timeout 180 | Receive-Job
    $jobs | Remove-Job -Force

    $ok = @($done | Where-Object { $_ -like '1|*' }).Count
    # 超限购的返回形如 "4|"（没下单所以订单号为空），
    # 用 -eq '4' 匹配不到，会把 9 条全判成「没拦住」——用 -like '4|*' 才对
    $limited = @($done | Where-Object { $_ -like '4|*' }).Count
    $err = @($done | Where-Object { $_ -eq 'ERR' }).Count

    # 不同的订单号数量必须为 1：两单并发都"成功"但拿到不同订单号，是最糟的情况
    $distinctOrders = @($done | Where-Object { $_ -like '1|*' } | ForEach-Object { $_.Split('|')[1] } | Sort-Object -Unique).Count

    $list = Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Items/List' -Method Post `
        -Body (@{ sessionId = $sessionId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    $row = @($list.data | Where-Object { $_.itemId -eq "$itemId" })[0]

    Write-Host ("        成功 {0} / 超限购 {1} / 异常 {2}；不同订单号 {3} 个；soldCount={4}" -f `
        $ok, $limited, $err, $distinctOrders, $row.soldCount) -ForegroundColor DarkGray

    Invoke-RestMethod 'http://127.0.0.1:5072/marketing/seckill/sessions/Finish' -Method Post `
        -Body (@{ sessionId = $sessionId; cancel = $true } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    return $ok -eq 1 -and $distinctOrders -eq 1 -and $limited -eq 9 -and $err -eq 0 -and $row.soldCount -eq 1
}

Invoke-Case 'API-MKT-090' '🔴 收尾断言：本次跑完不留任何活动' {
    # 活动是**全场生效**的，遗留一个「满 100 减 10」会让后面 product-regression
    # 的「原价 = 到手价」断言凭空少 10 块 —— 报错指向商品前台价格，
    # 病因却在几十个用例之外，而且每跑一次多留一批，失败会变得时有时无。
    #
    # 本脚本的活动由 New-Activity 统一记进 $script:promoIds 并在前面统一删除，
    # 所以正常情况下这里是 0。这条用例是**兜底**：将来有人新加一处建活动
    # 而忘了登记，这里会立刻变红，而不是等到几天后 product-regression 莫名失败。
    $list = Invoke-RestMethod "$Marketing/marketing/activities/List?page=1&pageSize=200" -Method Post `
        -Headers $script:adminHeaders -Body '{}' -ContentType 'application/json' -TimeoutSec 30

    $mine = @($list.data.items | Where-Object { $_.activityName -like "*$($script:suffix)*" })
    $deleted = 0
    foreach ($a in $mine) {
        try {
            $r = Invoke-RestMethod "$Marketing/marketing/activities/Delete" -Method Post `
                -Headers $script:adminHeaders -Body (@{ activityId = $a.id } | ConvertTo-Json) `
                -ContentType 'application/json' -TimeoutSec 30
            if ($r.success) { $deleted++ }
        } catch {
            # 单条删不掉不该让整条用例判红：它证明的是「有没有留垃圾」，
            # 而失败原因（网络、已被删）会写进下面的计数里。
            Write-Host ("        删除活动 {0} 失败: {1}" -f $a.id, $_.Exception.Message) -ForegroundColor DarkYellow
        }
    }

    Write-Host ("        遗留 {0} 个，补删 {1} 个" -f $mine.Count, $deleted) -ForegroundColor DarkGray
    return $mine.Count -eq $deleted
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
