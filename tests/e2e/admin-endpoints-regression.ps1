<#
.SYNOPSIS
    后台新增端点的回归测试（支付列表 / 券 CRUD / 物流公司 / 积分规则 / 搜索索引）。
.DESCRIPTION
    这些端点是本轮为了补齐权限点而新增的。它们共同的失败模式是「写完了但 DI 没注册」
    —— 那种情况下服务能起、能编译，只有真正打这个请求才会 500 或 404。
    所以本脚本的每一条都**真的打一次接口**并断言业务码，而不是只看返回非空。

    另外用一个**只有只读权限的受限账号**验证鉴权：超管有全部权限，
    拿超管测永远发现不了「这个接口其实没绑权限点」。
#>
[CmdletBinding()]
param(
    [string]$Gateway = 'http://127.0.0.1:5008',
    [string]$AuthService = 'http://127.0.0.1:5019',
    [string]$UserService = 'http://127.0.0.1:5011',
[string]$Marketing = 'http://127.0.0.1:5072',
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
        }
        else {
            $script:fail++
            $script:failures += "$Id $Name"
            Write-Host ("  FAIL  " + $Id + "  " + $Name) -ForegroundColor Red
            if ($StopOnFail) { throw "用例 $Id 失败，已按 -StopOnFail 中止" }
        }
    }
    catch {
        $script:fail++
        $script:failures += "$Id $Name (异常: $($_.Exception.Message))"
        Write-Host ("  FAIL  " + $Id + "  " + $Name + "  " + $_.Exception.Message) -ForegroundColor Red
        if ($StopOnFail) { throw }
    }
}

# ---------------------------------------------------------------- 令牌
function Get-AdminToken {
    $body = "grant_type=password&client_id=admin-app&username=$AdminUser&password=$AdminPassword"
    $r = Invoke-RestMethod -Uri "$AuthService/connect/token" -Method Post -Body $body `
        -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 20
    return $r.access_token
}

$token = Get-AdminToken
$auth = @{ Authorization = "Bearer $token" }

# 本次运行的后缀：测试数据用**唯一名字**，免得并发或重跑时互相踩。
# 原来各用例用时间戳拼名字，券模板那几个新用例要按名字回查，统一走这一份。
$script:suffix = Get-Random -Minimum 100000 -Maximum 999999

# 把 errors 里所有字段级错误文案拼成一段。
# 两种形状都要兼容：Post-Ep 解出来的是 PSCustomObject，
# 直接 Invoke-RestMethod 的也是 PSCustomObject，但键大小写可能不同。
function Get-ErrorText($resp) {
    if ($null -eq $resp.errors) { return '' }
    $errs = $resp.errors
    if ($errs -is [System.Collections.IDictionary]) {
        return (@($errs.Values | ForEach-Object { $_ }) -join ' ')
    }
    return (@($errs.PSObject.Properties | ForEach-Object { $_.Value }) -join ' ')
}

function Post-Ep {
    param([string]$Path, [hashtable]$Body, [hashtable]$Headers)
    if ($null -eq $Headers) { $Headers = $auth }
    try {
        return Invoke-RestMethod -Uri ($Gateway + $Path) -Method Post `
            -Body ($Body | ConvertTo-Json -Compress -Depth 8) `
            -Headers $Headers -ContentType 'application/json' -TimeoutSec 25
    }
    catch {
        # 业务失败与参数校验失败都是 HTTP 400 + { success:false, code:400, errors:{...} }。
        # Invoke-RestMethod 会把它变成异常并把 body 放进 ErrorDetails，
        # **必须把 body 解回来**：否则 code 永远是 -1，
        # 「校验器到底有没有生效」这种用例就永远失败——而且失败原因看起来
        # 像是校验器没注册，其实是测试自己没读响应体。
        $parsed = $null
        $raw = $_.ErrorDetails.Message
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            try { $parsed = $raw | ConvertFrom-Json } catch { $parsed = $null }
        }

        if ($parsed) { return $parsed }

        return [pscustomobject]@{ Success = $false; Code = -1; Message = $_.Exception.Message; Data = $null }
    }
}

function Post-EpStatus {
    param([string]$Path, [hashtable]$Body, [hashtable]$Headers)
    try {
        $r = Invoke-WebRequest -Uri ($Gateway + $Path) -Method Post `
            -Body ($Body | ConvertTo-Json -Compress -Depth 8) `
            -Headers $Headers -ContentType 'application/json' -TimeoutSec 25
        return [int]$r.StatusCode
    }
    catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        return -1
    }
}

Write-Host "`n=== 支付 / 退款（PermissionService 之外，补 admin/payments 与 refunds/Detail）===" -ForegroundColor Cyan

Invoke-Case 'API-ADM-001' 'admin/payments/List 返回成功且带分页信封' {
    $r = Post-Ep '/gateway/admin/payments/List' @{ page = 1; pageSize = 5 }
    $r.Success -and $r.data -ne $null -and $r.data.items -ne $null
}

Invoke-Case 'API-ADM-002' 'admin/payments/List 支持按状态筛选' {
    $r = Post-Ep '/gateway/admin/payments/List' @{ page = 1; pageSize = 5; status = 20 }
    # 断言「筛出来的每一行状态都是 20」，而不是「结果为空」——
    # 后者只在库里恰好没有已支付单时才成立，一旦有数据就误报。
    $r.Success -and @($r.data.items).Count -gt 0 -and
        @($r.data.items | Where-Object { $_.status -ne 20 }).Count -eq 0
}

Invoke-Case 'API-ADM-003' 'admin/payments/List 拒绝越界页码（校验器生效）' {
    $r = Post-Ep '/gateway/admin/payments/List' @{ page = 0; pageSize = 5 }
    -not $r.Success -and $r.Code -eq 400
}

Invoke-Case 'API-ADM-004' 'admin/payments/List 拒绝超长关键词（校验器生效）' {
    $r = Post-Ep '/gateway/admin/payments/List' @{ page = 1; pageSize = 5; keyword = ('x' * 200) }
    -not $r.Success -and $r.Code -eq 400
}

Invoke-Case 'API-ADM-005' 'refunds/Detail 不存在的单返回 404 而不是空对象' {
    $r = Post-Ep '/gateway/refunds/Detail' @{ refundId = 999999999 }
    -not $r.Success -and $r.Code -eq 404
}

Invoke-Case 'API-ADM-006' 'refunds/Detail 拒绝非正 Id' {
    $r = Post-Ep '/gateway/refunds/Detail' @{ refundId = 0 }
    -not $r.Success -and $r.Code -eq 400
}

Write-Host "`n=== 券模板 / 券活动 / 核销记录 ===" -ForegroundColor Cyan

Invoke-Case 'API-ADM-010' 'coupon-templates/List 返回成功（此前无此端点）' {
    $r = Post-Ep '/gateway/marketing/coupon-templates/List' @{ page = 1; pageSize = 5 }
    $r.Success -and $r.data -ne $null
}

Invoke-Case 'API-ADM-011' 'coupon-templates/List 拒绝超范围券类型' {
    $r = Post-Ep '/gateway/marketing/coupon-templates/List' @{ page = 1; pageSize = 5; couponType = 99 }
    -not $r.Success -and $r.Code -eq 400
}

Invoke-Case 'API-ADM-012' 'coupon-activities/List 返回成功且带模板名' {
    $r = Post-Ep '/gateway/marketing/coupon-activities/List' @{ page = 1; pageSize = 5 }
    $r.Success -and $r.data -ne $null
}

Invoke-Case 'API-ADM-013' 'coupon-records/List 返回成功' {
    $r = Post-Ep '/gateway/marketing/coupon-records/List' @{ page = 1; pageSize = 5 }
    $r.Success -and $r.data -ne $null
}

Invoke-Case 'API-ADM-014' 'coupon-templates/Update 拒绝不存在的模板' {
    $r = Post-Ep '/gateway/marketing/coupon-templates/Update' @{
        templateId = 999999999; templateName = '不存在的券'; couponType = 1
        thresholdAmount = 100; discountAmount = 10
    }
    -not $r.Success -and $r.Code -eq 404
}

Invoke-Case 'API-ADM-015' '满减券缺门槛金额被拒（条件式校验生效）' {
    $r = Post-Ep '/gateway/marketing/coupon-templates/Update' @{
        templateId = 1; templateName = '条件校验用例'; couponType = 1
        thresholdAmount = 0; discountAmount = 10
    }
    -not $r.Success -and $r.Code -eq 400
}

Invoke-Case 'API-ADM-016' '满赠券未选赠送模板被拒' {
    $r = Post-Ep '/gateway/marketing/coupon-templates/Update' @{
        templateId = 1; templateName = '满赠校验用例'; couponType = 4; giftTemplateId = 0
    }
    -not $r.Success -and $r.Code -eq 400
}

Invoke-Case 'API-ADM-017' '🔴 新建满赠券未选赠送模板被拒（创建路径也要走校验）' {
    # 创建路径此前把实体直接 Insert，绕过了全部校验：满赠券可以不选赠送模板就建出来，
    # 用户付完钱才发现赠品券发不出。编辑路径有校验、创建路径没有，缺陷只会从松的那侧漏。
    $r = Post-Ep '/gateway/marketing/coupon-templates/Create' @{
        templateName = "缺赠送$($script:suffix)"; couponType = 4; validDays = 30
        totalQuantity = 10; perUserLimit = 1; perOrderLimit = 1; platformId = 0; status = 1
    }
    -not $r.Success -and $r.Code -eq 400 -and (Get-ErrorText $r) -match '赠送的券模板'
}

Invoke-Case 'API-ADM-018' '🔴 新建满赠券的赠送模板不存在被拒（404）' {
    $r = Post-Ep '/gateway/marketing/coupon-templates/Create' @{
        templateName = "赠品不存在$($script:suffix)"; couponType = 4
        giftTemplateId = 999999999999; validDays = 30
        totalQuantity = 10; perUserLimit = 1; perOrderLimit = 1; platformId = 0; status = 1
    }
    -not $r.Success -and $r.Code -eq 404
}

Invoke-Case 'API-ADM-019' '🔴 新建满减券没填优惠金额被拒（否则命中却不减钱）' {
    $r = Post-Ep '/gateway/marketing/coupon-templates/Create' @{
        templateName = "空金额$($script:suffix)"; couponType = 1; thresholdAmount = 100
        discountAmount = 0; validDays = 30; totalQuantity = 10
        perUserLimit = 1; perOrderLimit = 1; platformId = 0; status = 1
    }
    -not $r.Success -and $r.Code -eq 400 -and (Get-ErrorText $r) -match '优惠金额'
}

Invoke-Case 'API-ADM-007' '🔴 新建时不能编造已发放数（IssuedQuantity 恒从 0 起）' {
    $name = "编造发放数$($script:suffix)"
    $created = Post-Ep '/gateway/marketing/coupon-templates/Create' @{
        templateName = $name; couponType = 1; thresholdAmount = 10; discountAmount = 1
        validDays = 30; totalQuantity = 100; issuedQuantity = 999
        perUserLimit = 1; perOrderLimit = 1; platformId = 0; status = 1
    }
    if (-not $created.Success) { return $false }

    $list = Post-Ep '/gateway/marketing/coupon-templates/List' @{ page = 1; pageSize = 50; keyword = $name }
    $row = @($list.data.items | Where-Object { $_.templateId -eq $created.data })[0]

    # 已发放数是发放流程累加出来的计数，不是表单字段：
    # 能写进来的话，报表上的「已发放」就成了可以随手编造的数字。
    $ok = $null -ne $row -and $row.issuedQuantity -eq 0
    Post-Ep '/gateway/marketing/coupon-templates/Delete' @{ templateId = $created.data } | Out-Null
    return $ok
}

Invoke-Case 'API-ADM-008' '🔴 新建券活动关联的模板不存在被拒（404）' {
    $r = Post-Ep '/gateway/marketing/coupon-activities/Create' @{
        activityName = "坏活动$($script:suffix)"; templateId = 999999999999
        claimStartTime = [DateTime]::UtcNow.ToString('o')
        claimEndTime = [DateTime]::UtcNow.AddDays(1).ToString('o')
        claimQuantity = 10; perUserLimit = 1; targetType = 1; targets = '[]'
        platformId = 0; status = 1
    }
    -not $r.Success -and $r.Code -eq 404
}

Invoke-Case 'API-ADM-009' '🔴 启用中的券活动还引用着模板时不许删模板（否则悬空引用）' {
    # 模板被删、活动还在的后果：领券中心照样列出这个活动（模板名退化成「券模板」），
    # 用户点「领取」报「券模板不存在或已停用」，而运营连停用这个活动都改不动。
    $name = "引用中的模板$($script:suffix)"
    $template = Post-Ep '/gateway/marketing/coupon-templates/Create' @{
        templateName = $name; couponType = 1; thresholdAmount = 10; discountAmount = 1
        validDays = 30; totalQuantity = 100; perUserLimit = 1; perOrderLimit = 1
        platformId = 0; status = 1
    }
    if (-not $template.Success) { return $false }

    $activity = Post-Ep '/gateway/marketing/coupon-activities/Create' @{
        activityName = "引用活动$($script:suffix)"; templateId = $template.data
        claimStartTime = [DateTime]::UtcNow.AddMinutes(-5).ToString('o')
        claimEndTime = [DateTime]::UtcNow.AddDays(1).ToString('o')
        claimQuantity = 10; perUserLimit = 1; targetType = 1; targets = '[]'
        platformId = 0; status = 1
    }
    if (-not $activity.Success) { return $false }

    $blocked = Post-Ep '/gateway/marketing/coupon-templates/Delete' @{ templateId = $template.data }
    $blockedOk = -not $blocked.Success -and $blocked.Code -eq 400

    # 停用活动之后就能删了：停用的活动不会出现在领券中心，拿它挡着删除没有意义
    Post-Ep '/gateway/marketing/coupon-activities/Update' @{
        activityId = $activity.data; activityName = "引用活动$($script:suffix)"
        templateId = $template.data
        claimStartTime = [DateTime]::UtcNow.AddMinutes(-5).ToString('o')
        claimEndTime = [DateTime]::UtcNow.AddDays(1).ToString('o')
        claimQuantity = 10; perUserLimit = 1; targetType = 1; targets = '[]'
        sortOrder = 0; status = 2
    } | Out-Null
    $deleted = Post-Ep '/gateway/marketing/coupon-templates/Delete' @{ templateId = $template.data }

    return $blockedOk -and $deleted.Success
}

Write-Host "`n=== 物流公司字典 ===" -ForegroundColor Cyan

Invoke-Case 'API-ADM-020' 'logistics-companies/List 返回内置的公司' {
    $r = Post-Ep '/gateway/logistics-companies/List' @{ page = 1; pageSize = 5 }
    $r.Success -and $r.data.items.Count -ge 5
}

Invoke-Case 'API-ADM-021' 'logistics-companies/Options 只回启用项' {
    $r = Post-Ep '/gateway/logistics-companies/Options' @{}
    $r.Success -and $r.data.Count -ge 5
}

Invoke-Case 'API-ADM-022' '新建物流公司 → 查得到 → 改名 → 删除（整条链路）' {
    $name = "回归测试快递$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"

    $created = Post-Ep '/gateway/logistics-companies/Create' @{ companyName = $name; platformId = 0; sortOrder = 999 }
    if (-not $created.Success) { return $false }
    $id = $created.data

    $list = Post-Ep '/gateway/logistics-companies/List' @{ page = 1; pageSize = 50; keyword = $name }
    $found = @($list.data.items | Where-Object { $_.companyName -eq $name })
    if ($found.Count -ne 1) { return $false }

    $upd = Post-Ep '/gateway/logistics-companies/Update' @{
        logisticsId = $id; companyName = ($name + '改名'); sortOrder = 998; status = 1
    }
    if (-not $upd.Success) { return $false }

    $after = Post-Ep '/gateway/logistics-companies/List' @{ page = 1; pageSize = 50; keyword = ($name + '改名') }
    if (@($after.data.items).Count -ne 1) { return $false }

    $del = Post-Ep '/gateway/logistics-companies/Delete' @{ logisticsId = $id }
    if (-not $del.Success) { return $false }

    # 软删后不该再出现在列表里（GlobalFilter 过滤 is_deleted）
    $gone = Post-Ep '/gateway/logistics-companies/List' @{ page = 1; pageSize = 50; keyword = ($name + '改名') }
    return @($gone.data.items).Count -eq 0
}

Invoke-Case 'API-ADM-023' '新建重名物流公司被拒（409）' {
    $r = Post-Ep '/gateway/logistics-companies/Create' @{ companyName = '顺丰速运'; platformId = 0 }
    -not $r.Success -and $r.Code -eq 409
}

Invoke-Case 'API-ADM-024' '物流公司名超长被拒' {
    $r = Post-Ep '/gateway/logistics-companies/Create' @{ companyName = ('x' * 100); platformId = 0 }
    -not $r.Success -and $r.Code -eq 400
}

Write-Host "`n=== 积分规则 ===" -ForegroundColor Cyan

Invoke-Case 'API-ADM-030' 'points/Rules 返回当前规则与规格默认值' {
    $r = Post-Ep '/gateway/points/Rules' @{}
    $r.Success -and $r.data -ne $null -and $r.data.balanceCap -gt 0 -and $r.data.validDaysDefault -gt 0
}

Invoke-Case 'API-ADM-031' '积分规则保存后立即生效（缓存已失效）' {
    $before = Post-Ep '/gateway/points/Rules' @{}
    if (-not $before.Success) { return $false }

    $newCap = [long]($before.data.balanceCap) + 12345
    $save = Post-Ep '/gateway/points/SaveRules' @{
        balanceCap       = $newCap
        validDays        = $before.data.validDays
        registerGift     = $before.data.registerGift
        firstEvaluateGift = $before.data.firstEvaluateGift
        pointsPerYuan    = $before.data.pointsPerYuan
        earnPointsPerYuan = $before.data.earnPointsPerYuan
        signInRewards    = @($before.data.signInRewards)
    }
    if (-not $save.Success) { return $false }

    # 不等 30 秒：保存后必须立刻读到新值，否则就是「配置存了但不生效」
    $after = Post-Ep '/gateway/points/Rules' @{}
    if ([long]$after.data.balanceCap -ne $newCap) { return $false }

    # 还原，避免污染后续测试
    $restore = Post-Ep '/gateway/points/SaveRules' @{
        balanceCap       = $before.data.balanceCapDefault
        validDays        = $before.data.validDaysDefault
        registerGift     = $before.data.registerGiftDefault
        firstEvaluateGift = $before.data.firstEvaluateGiftDefault
        pointsPerYuan    = $before.data.pointsPerYuanDefault
        earnPointsPerYuan = $before.data.earnPointsPerYuanDefault
        signInRewards    = @($before.data.signInRewardsDefault)
    }
    return $restore.Success
}

Invoke-Case 'API-ADM-032' '签到奖励为空数组被拒' {
    $r = Post-Ep '/gateway/points/SaveRules' @{
        balanceCap = 1000; validDays = 365; registerGift = 100; firstEvaluateGift = 20
        pointsPerYuan = 100; earnPointsPerYuan = 1; signInRewards = @()
    }
    -not $r.Success -and $r.Code -eq 400
}

Invoke-Case 'API-ADM-033' '签到奖励含负数被拒' {
    $r = Post-Ep '/gateway/points/SaveRules' @{
        balanceCap = 1000; validDays = 365; registerGift = 100; firstEvaluateGift = 20
        pointsPerYuan = 100; earnPointsPerYuan = 1; signInRewards = @(1, -5, 3)
    }
    -not $r.Success -and $r.Code -eq 400
}

Invoke-Case 'API-ADM-034' '有效期 0 天被拒' {
    $r = Post-Ep '/gateway/points/SaveRules' @{
        balanceCap = 1000; validDays = 0; registerGift = 100; firstEvaluateGift = 20
        pointsPerYuan = 100; earnPointsPerYuan = 1; signInRewards = @(1, 2, 3)
    }
    -not $r.Success -and $r.Code -eq 400
}

Write-Host "`n=== 搜索索引 ===" -ForegroundColor Cyan

Invoke-Case 'API-ADM-040' 'SearchIndex/Reconcile 只读对账返回成功' {
    $r = Post-Ep '/gateway/products/SearchIndex/Reconcile' @{ pageSize = 50 }
    $r.Success -and $r.data -ne $null -and $r.data.consistent -ne $null
}

Invoke-Case 'API-ADM-041' 'SearchIndex/Reconcile 拒绝越界批量' {
    $r = Post-Ep '/gateway/products/SearchIndex/Reconcile' @{ pageSize = 9999 }
    -not $r.Success -and $r.Code -eq 400
}

Write-Host "`n=== 商品新建（与编辑分开的端点）===" -ForegroundColor Cyan

Invoke-Case 'API-ADM-050' 'products/Create 空字段返回 **400** 而不是 500' {
    # 这里必须断言 400 而不是「非 200」：
    # 踩过的坑是校验器写成 NotNull().Must(x => x!.Count > 0)，
    # FluentValidation 默认 Continue 级联让 Must 在 null 上执行 → NullReferenceException → 500。
    # 断言「非 200」的话 500 也会算通过，缺陷就漏过去了。详见 CODING_STANDARD 第 68 条。
    (Post-EpStatus '/gateway/products/Create' `
        @{ spuName = ''; categoryId = 0; deliveryType = 1; mainImage = '' } $auth) -eq 400
}

Invoke-Case 'API-ADM-051' 'Create 与 Save 缺规格项时都返回 400（规则共享，没漂移）' {
    $payload = @{
        spuName = '校验一致性用例'; categoryId = 1; deliveryType = 1; mainImage = 'http://x/y.jpg'
    }
    $create = Post-EpStatus '/gateway/products/Create' $payload $auth
    $save = Post-EpStatus '/gateway/products/Save' $payload $auth
    # 两边都必须回 400。只回「非 200」会漏掉「两边都 500」这种同样算通过的情况。
    ($create -eq 400) -and ($save -eq 400)
}

Invoke-Case 'API-ADM-052' 'products/Save 非法配送方式返回 400' {
    (Post-EpStatus '/gateway/products/Save' `
        @{ spuName = '配送校验用例'; categoryId = 1; deliveryType = 99; mainImage = 'http://x/y.jpg' } $auth) -eq 400
}

Invoke-Case 'API-ADM-053' 'coupons/Settle 空订单行返回 400（同一类缺陷的回归）' {
    # 直连营销服务而不是走网关：/gateway/coupons/* 是**C 端**接口
    # （客户令牌访问，刻意不绑后台权限点），拿后台令牌走网关现在会被
    # fail-closed 挡成 403，那样这条断言测的就成了鉴权而不是校验器。
    # 这条用例要证明的是「空订单行会被校验器挡住」，与谁调用无关。
    try {
        Invoke-RestMethod "$Marketing/coupons/Settle" -Method Post `
            -Body (@{ customerId = 0; lines = $null } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 25 | Out-Null
        return $false
    } catch {
        return [int]$_.Exception.Response.StatusCode -eq 400
    }
}

Write-Host "`n=== 文件管理 / 积分流水（补齐占位页所需的后端）===" -ForegroundColor Cyan

Invoke-Case 'API-ADM-070' 'files/List 返回分页结果（含可读大小与分类中文名）' {
    $r = Post-Ep '/gateway/files/List' @{ page = 1; pageSize = 5 }
    # 显式加括号：PowerShell 里 -and 与 -or **优先级相同、左结合**，
    # 写成 `A -and B -or C` 会被解析成 `(A -and B) -or C` —— 于是 C 一真就整体通过，
    # 前面的断言全部白写，测试变成永远绿的摆设。
    if (-not ($r.Success -and $r.data -ne $null)) { return $false }

    $items = @($r.data.items)
    if ($items.Count -eq 0) { return $true }

    return ($items[0].sizeText -match '\s(B|KB|MB|GB)$') -and ([bool]$items[0].categoryName)
}

Invoke-Case 'API-ADM-071' 'files/List 拒绝未知分类（ToolService 此前完全没有校验管道）' {
    # 这一条同时守住 ToolService 的校验管道：它以前只注册了 AddMediatR，
    # 没注册 AddValidatorsFromAssembly 与 ValidationBehavior，命令一条校验都不跑。
    (Post-EpStatus '/gateway/files/List' @{ page = 1; pageSize = 5; category = 'not-a-category' } $auth) -eq 400
}

Invoke-Case 'API-ADM-072' 'files/List 拒绝越界页码' {
    (Post-EpStatus '/gateway/files/List' @{ page = 0; pageSize = 5 } $auth) -eq 400
}

Invoke-Case 'API-ADM-073' 'files/Delete 不存在的文件返回 404' {
    $r = Post-Ep '/gateway/files/Delete' @{ fileId = 999999999 }
    -not $r.Success -and $r.Code -eq 404
}

Invoke-Case 'API-ADM-074' 'points/RecordsAll 跨客户返回流水且带动作中文名' {
    $r = Post-Ep '/gateway/points/RecordsAll' @{ page = 1; pageSize = 5 }
    if (-not ($r.Success -and $r.data -ne $null)) { return $false }

    $items = @($r.data.items)
    if ($items.Count -eq 0) { return $true }

    return [bool]$items[0].actionName
}

Invoke-Case 'API-ADM-075' 'points/RecordsAll 按动作筛选生效' {
    $r = Post-Ep '/gateway/points/RecordsAll' @{ page = 1; pageSize = 5; action = 'signin' }
    $r.Success -and @($r.data.items | Where-Object { $_.action -ne 'signin' }).Count -eq 0
}

Invoke-Case 'API-ADM-076' 'points/RecordsAll 拒绝越界每页条数' {
    (Post-EpStatus '/gateway/points/RecordsAll' @{ page = 1; pageSize = 5000 } $auth) -eq 400
}

Write-Host "`n=== 鉴权：受限账号必须被拒 ===" -ForegroundColor Cyan

# 造一个「什么都读不了」的账号，用它验证上面这些端点确实绑了权限点。
# 超管有全部权限，用超管测这个等于没测。
#
# 账号必须至少绑 1 个角色（DATA_SPEC 5.18），所以先建一个**不绑任何权限点**的空角色。
# 这样账号是「有角色但没权限」，fail-closed 的判定路径与「没有角色」完全一致，
# 而且不会绕过建号接口的必填规则。
$readonlyUser = 'admreadonly' + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$pwd = 'Readonly123456'
# 手机号必须每次都不同：建号接口对 phone 有唯一约束，
# 固定值会让第二次运行必然失败（症状是「手机号已被占用」，与被测逻辑毫无关系，
# 极容易被误判成账号接口坏了）。取 139 + 时间戳后 8 位，落在 ^1[3-9]\d{9}$ 内。
$readonlyPhone = '139' + ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString().Substring(5, 8))

$emptyRole = Post-Ep '/gateway/roles/Create' @{
    roleName = "只读空角色$($script:suffix)"; code = "roempty$($script:suffix)"
    allowedScopes = 1; dataScope = 1; remark = '回归用：不绑任何权限点'
}
$emptyRoleId = if ($emptyRole.Success) { [long]$emptyRole.data } else { 0 }
if ($emptyRoleId -le 0) {
    Write-Host ("  （建空角色失败：" + $emptyRole.Message + "，只读账号将拿不到角色）") -ForegroundColor DarkYellow
}

# 走网关建号：租户锁定（DATA_SPEC 5.18）要求调用方身份，直连服务端口没有 X-Claim-* 头会被拒。
$createUser = Post-Ep '/gateway/users/Create' @{
    userName   = $readonlyUser
    password   = $pwd
    phone      = $readonlyPhone
    tenantType = 1
    nickName   = '只读回归账号'
    platformId = 0
    roleIds    = @($emptyRoleId)
}

Invoke-Case 'API-ADM-060' '创建只读测试账号成功' {
    $createUser.Success
}

$roToken = $null
try {
    $roToken = (Invoke-RestMethod -Uri "$AuthService/connect/token" -Method Post `
        -Body "grant_type=password&client_id=admin-app&username=$readonlyUser&password=$pwd" `
        -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 20).access_token
}
catch {
    Write-Host ("  （拿只读账号令牌失败：" + $_.Exception.Message + "）") -ForegroundColor DarkYellow
}

$roAuth = if ($roToken) { @{ Authorization = "Bearer $roToken" } } else { $null }

if ($roAuth) {
    # 无任何权限点 → 全部应 403。
    # 注意：这里**必须**断言 403 而不是「非 200」——
    # 端点根本没接上转发时会返回 404/500，那同样是缺陷，但原因不同。
    foreach ($probe in @(
        @{ Id = 'API-ADM-061'; Path = '/gateway/admin/payments/List'; Body = @{ page = 1; pageSize = 5 } },
        @{ Id = 'API-ADM-062'; Path = '/gateway/marketing/coupon-templates/List'; Body = @{ page = 1; pageSize = 5 } },
        @{ Id = 'API-ADM-063'; Path = '/gateway/logistics-companies/List'; Body = @{ page = 1; pageSize = 5 } },
        @{ Id = 'API-ADM-064'; Path = '/gateway/points/Rules'; Body = @{} },
        @{ Id = 'API-ADM-065'; Path = '/gateway/products/SearchIndex/Reconcile'; Body = @{ pageSize = 10 } }
        @{ Id = 'API-ADM-066'; Path = '/gateway/files/List'; Body = @{ page = 1; pageSize = 5 } },
        @{ Id = 'API-ADM-067'; Path = '/gateway/points/RecordsAll'; Body = @{ page = 1; pageSize = 5 } }
    )) {
        $probeId = $probe.Id
        $probePath = $probe.Path
        $probeBody = $probe.Body
        Invoke-Case $probeId ("无权限账号访问 " + $probePath + " 被拒 403") {
            (Post-EpStatus $probePath $probeBody $roAuth) -eq 403
        }
    }
}
else {
    Write-Host '  跳过鉴权用例：未拿到只读账号令牌' -ForegroundColor Yellow
}

# ---- 新建平台：仅超管，且**不看权限点** ----
#
# 这条要验的是「第二道闸」。网关的 RBAC 只回答「你有没有 platform:create」，
# 而权限点是运行时可配置的实体 —— 某个平台角色被勾上它之后，网关就会放行。
# 所以服务端还有一层 ISuperAdminOnly：只看租户身份（PlatformId = 0），不看权限点。
#
# 造一个**平台维度**的账号（PlatformId != 0）并绑上内置的「平台管理员」角色
# （该角色绑定了全部权限点，包括 platform:create），
# 然后用它去建平台 —— 必须被 403，而不是 200。
$platAdminUser = 'admplatadmin' + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$platAdminPwd = 'PlatAdmin123456'

$firstPlatform = (Post-Ep '/gateway/platforms/List' @{ page = 1; pageSize = 1 }).data.items[0]

# 同样走网关：直连服务端口没有租户头，建号会被租户锁定拒掉（403）。
$platCreate = Post-Ep '/gateway/users/Create' @{
    userName   = $platAdminUser
    password   = $platAdminPwd
    phone      = '137' + ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString().Substring(5, 8))
    tenantType = 1
    nickName   = '平台越权用例'
    platformId = $firstPlatform.id
    roleIds    = @(9001)
}

$platToken = $null
if ($platCreate.Success) {
    try {
        $platToken = (Invoke-RestMethod -Uri "$AuthService/connect/token" -Method Post `
            -Body "grant_type=password&client_id=admin-app&username=$platAdminUser&password=$platAdminPwd" `
            -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 20).access_token
    }
    catch { $platToken = $null }
}

# 超管账号 Id：下面的越权用例要验「平台账号碰不到超管」。
# 用超管令牌查得到（它不受平台裁剪），用平台令牌查不到 —— 这本身就是 API-ADM-088 要验的事。
$superAdminId = 0
try {
    $superRow = (Invoke-RestMethod "$Gateway/gateway/users/List?page=1&pageSize=50&keyword=$AdminUser" `
        -Headers $auth -TimeoutSec 20).data |
        Where-Object { $_.userName -eq $AdminUser } | Select-Object -First 1
    if ($superRow) { $superAdminId = [long]$superRow.id }
}
catch { $superAdminId = 0 }

if ($platToken) {
    $platAuth = @{ Authorization = "Bearer $platToken" }

    Invoke-Case 'API-ADM-080' '平台账号即使持有 platform:create 也不能新建平台（403）' {
        $status = Post-EpStatus '/gateway/platforms/Create' @{
            platformName  = "越权平台$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
            platformCode  = 'ABCDEF'
            contactName   = '越权'
            contactPhone  = '13800000000'
        } $platAuth
        $status -eq 403
    }

    Invoke-Case 'API-ADM-081' '平台账号可以编辑**自己**的平台（租户过滤生效）' {
        # 「平台可以自己编辑自己的信息」：这条验证它确实能做，
        # 免得为了堵越权把正常的自助编辑也一起堵死。
        #
        # ⚠️ 必须把**整条**记录回传：UpdatePlatformCommand 的校验要求
        # mallName / 三档颜色都非空，它是「整条更新」不是「局部更新」。
        # 只传自己关心的两三个字段会被 400 挡下，看起来像「平台改不了自己的信息」，
        # 实际是前端漏传了字段。（前端对应做法：编辑页先把整条读出来再整体提交。）
        $r = Post-Ep '/gateway/platforms/Update' @{
            platformId       = $firstPlatform.id
            platformName     = $firstPlatform.platformName
            platformCode     = $firstPlatform.platformCode
            contactName      = $firstPlatform.contactName
            contactPhone     = $firstPlatform.contactPhone
            mallName         = $firstPlatform.mallName
            logo             = $firstPlatform.logo
            notice           = $firstPlatform.notice
            primaryColor     = $firstPlatform.primaryColor
            tabColor         = $firstPlatform.tabColor
            backgroundColor  = $firstPlatform.backgroundColor
            shippingFee      = $firstPlatform.shippingFee
            freeShippingThreshold = $firstPlatform.freeShippingThreshold
            status           = $firstPlatform.status
            remark           = $firstPlatform.remark
        } $platAuth
        $r.Success
    }

    Invoke-Case 'API-ADM-082' '平台账号改不了**别人**的平台（租户过滤挡住）' {
        # 找一个不属于自己平台的 Id。找不到就跳过（只有 1 个平台时无从验证）。
        $others = @((Post-Ep '/gateway/platforms/List' @{ page = 1; pageSize = 50 }).data.items |
            Where-Object { $_.id -ne $firstPlatform.id })
        if ($others.Count -eq 0) { return $true }

        # 同样回传整条记录：否则失败可能来自校验（400），
        # 而不是来自租户过滤 —— 那就成了「假通过」：
        # 断言 -not success 时，参数填错也会让它变绿。
        $other = $others[0]
        $r = Post-Ep '/gateway/platforms/Update' @{
            platformId       = $other.id
            platformName     = $other.platformName
            platformCode     = $other.platformCode
            contactName      = $other.contactName
            contactPhone     = $other.contactPhone
            mallName         = $other.mallName
            logo             = $other.logo
            notice           = $other.notice
            primaryColor     = $other.primaryColor
            tabColor         = $other.tabColor
            backgroundColor  = $other.backgroundColor
            shippingFee      = $other.shippingFee
            freeShippingThreshold = $other.freeShippingThreshold
            status           = $other.status
            remark           = $other.remark
        } $platAuth
        # 行级过滤让这条 UPDATE 影响 0 行，Handler 会回「平台不存在」而不是成功
        -not $r.Success
    }

    # ---- 账号管理的租户锁定（DATA_SPEC 5.18）----
    #
    # 这一组挡的是**提权**，不是普通的越权读：
    # 内置的「平台管理员」角色按 DATA_SPEC 5.21 绑定了全部权限点，
    # 所以平台账号天然持有 user:create / user:update，网关那一层拦不住它。
    # 只要服务端不再看一次租户身份，平台账号就能：
    #   ① 建一个 platformId=0 的账号再绑 platform-admin → 一步变超管；
    #   ② 重置超管的密码后直接登录 → 账号接管。
    # 两条都必须被 403/404 挡住，而不是靠「前端不这么传」。

    Invoke-Case 'API-ADM-090' '🔴 平台账号建 platformId=0 的账号被拒（提权封堵）' {
        $r = Post-Ep '/gateway/users/Create' @{
            userName   = "esc$($script:suffix)"
            password   = 'Test123456'
            phone      = '134' + ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString().Substring(5, 8))
            tenantType = 1
            nickName   = '提权尝试'
            platformId = 0
            roleIds    = @(9001)
        } $platAuth
        (-not $r.Success) -and ([int]$r.code -eq 403)
    }

    Invoke-Case 'API-ADM-091' '🔴 商户角色不能绑给平台账号（400 且不产生账号）' {
        # 9005 = merchant-operator，AllowedScopes=2（只允许商户账号）
        $un = "scp$($script:suffix)"
        $r = Post-Ep '/gateway/users/Create' @{
            userName   = $un
            password   = 'Test123456'
            phone      = '133' + ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString().Substring(5, 8))
            tenantType = 1
            nickName   = '作用域不符'
            platformId = $firstPlatform.id
            roleIds    = @(9005)
        } $platAuth
        if ($r.Success -or [int]$r.code -ne 400) { return $false }

        # 关键：预检必须发生在插库**之前**。否则账号已经建出来了，
        # 只是没绑上角色 —— 运营看到「创建成功」，拿到的却是个什么都做不了的账号。
        #
        # ⚠️ users/List 的 data 是**裸数组**（不是 {items:[...]} 信封），
        # 而且 @($null).Count 在 PowerShell 里等于 1 —— 写成 .data.items 会永远「查到 1 条」，
        # 这条断言就变成了永远失败（或换个写法后永远通过）的假用例。
        $check = Invoke-RestMethod "$Gateway/gateway/users/List?page=1&pageSize=50&keyword=$un" `
            -Headers $auth -TimeoutSec 20
        @($check.data).Count -eq 0
    }

    Invoke-Case 'API-ADM-092' '🔴 平台账号重置超管密码被拒（404，账号接管封堵）' {
        if ($superAdminId -le 0) { return $true }
        $r = Post-Ep '/gateway/users/ResetPassword' @{
            userId = $superAdminId; newPassword = 'Hijacked123456'
        } $platAuth
        # 按 TEST_CASES 6.2：越权访问他人资源回 404 而不是 403，避免泄露账号是否存在
        (-not $r.Success) -and ([int]$r.code -eq 404)
    }

    Invoke-Case 'API-ADM-093' '🔴 平台账号停用超管被拒（404）' {
        if ($superAdminId -le 0) { return $true }
        $r = Post-Ep '/gateway/users/UpdateStatus' @{ userId = $superAdminId; status = 2 } $platAuth
        (-not $r.Success) -and ([int]$r.code -eq 404)
    }

    Invoke-Case 'API-ADM-094' '平台账号的账号列表只含本平台（跨租户读封堵）' {
        # 传 platformId=0 试图「不限平台」：服务端必须用上下文里的平台覆盖入参。
        $r = Invoke-RestMethod "$Gateway/gateway/users/List?page=1&pageSize=50&platformId=0" `
            -Headers $platAuth -TimeoutSec 20
        $items = @($r.data)
        # 「全部平台」这个文案只可能来自 platformId=0 的账号（超管）
        $items.Count -gt 0 -and @($items | Where-Object { $_.platformName -eq '全部平台' }).Count -eq 0
    }

    Invoke-Case 'API-ADM-095' '🔴 直连服务端口建号被拒（没有网关租户头 = 无身份）' {
        # 注意断言 body 里的业务码，不是 HTTP 状态码：
        # 这套服务的业务失败统一回 **HTTP 200 + success=false**，
        # 断言状态码的话这条用例会「永远失败」或「永远通过」，与租户锁定毫无关系。
        $body = $null
        try {
            $body = Invoke-RestMethod -Uri "$UserService/users/Create" -Method Post `
                -Body (@{
                    userName = "direct$($script:suffix)"; password = 'Test123456'
                    phone = '132' + ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString().Substring(5, 8))
                    tenantType = 1; nickName = '直连尝试'; platformId = 0; roleIds = @(9001)
                } | ConvertTo-Json -Compress) `
                -ContentType 'application/json' -TimeoutSec 20
        }
        catch {
            $body = $null
        }
        $null -ne $body -and (-not $body.success) -and ([int]$body.code -eq 403)
    }
}
else {
    Write-Host '  跳过新建平台越权用例：未能建出平台维度的测试账号' -ForegroundColor Yellow
}

Invoke-Case 'API-ADM-083' 'roles/Detail 返回角色与已绑定权限点' {
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/roles/Detail?roleId=9001" `
        -Headers $auth -TimeoutSec 20
    $r.success -and $r.data.roleName -and @($r.data.permissionIds).Count -gt 0
}

Invoke-Case 'API-ADM-083b' '🔴 P0 roles/BindPermissions：绑上 → 读回一致 → 解绑' {
    # 这个端点在补这条之前**零覆盖**，而它正是「角色权限树多级勾选」的落库入口
    # （用户需求：权限树每个权限要支持增删改、要有全部权限按钮）。
    # 树的渲染再漂亮，勾完存不进去或存错，整套权限配置就是空转。
    $code = 'regbind' + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString()
    $created = Post-Ep '/gateway/roles/Create' @{
        roleName = "回归角色$code"; code = $code; allowedScopes = 1; dataScope = 1; remark = '临时'
    }
    if (-not $created.Success) { Write-Host ("        建角色失败: " + $created.Message) -ForegroundColor DarkYellow; return $false }
    $roleId = [long]$created.data

    try {
        # 拿两个真实存在的权限点：从内置角色的已绑定集合里取，避免自己造数据
        $detail = Invoke-RestMethod -Uri "$Gateway/gateway/roles/Detail?roleId=9001" -Headers $auth -TimeoutSec 20
        $ids = @($detail.data.permissionIds | Select-Object -First 2 | ForEach-Object { [long]$_ })
        if ($ids.Count -lt 2) { return $false }

        $bind = Post-Ep '/gateway/roles/BindPermissions' @{ roleId = $roleId; permissionIds = $ids }
        if (-not $bind.Success) { Write-Host ("        绑定失败: " + $bind.Message) -ForegroundColor DarkYellow; return $false }

        $after = Invoke-RestMethod -Uri "$Gateway/gateway/roles/Detail?roleId=$roleId" -Headers $auth -TimeoutSec 20
        $bound = @($after.data.permissionIds | ForEach-Object { [long]$_ }) | Sort-Object

        # 解绑：传空集合应当清空，而不是「不传就不动」
        $clear = Post-Ep '/gateway/roles/BindPermissions' @{ roleId = $roleId; permissionIds = @() }
        $cleared = Invoke-RestMethod -Uri "$Gateway/gateway/roles/Detail?roleId=$roleId" -Headers $auth -TimeoutSec 20

        Write-Host ("        绑 {0} 个 → 读回 {1} 个 → 解绑后 {2} 个" -f `
            $ids.Count, $bound.Count, @($cleared.data.permissionIds).Count) -ForegroundColor DarkGray

        return $clear.Success `
            -and ($bound -join ',') -eq ((@($ids) | Sort-Object) -join ',') `
            -and @($cleared.data.permissionIds).Count -eq 0
    } finally {
        # 角色不删会在权限列表里越积越多，而且它带着权限绑定，
        # 后面的「受限账号必须被拒」用例可能因为这个角色拿到额外权限而行为漂移。
        Post-Ep '/gateway/roles/Delete' @{ roleId = $roleId } | Out-Null
    }
}

Invoke-Case 'API-ADM-083c' '🔴 内置管理员角色的权限锁定，重绑被拒' {
    # 用户明确要求「禁止编辑内置管理员角色」。
    # 权限重绑是最容易被忽略的一条路径：改名字被拒了，但把超管的权限清空一样是破坏。
    $r = Post-Ep '/gateway/roles/BindPermissions' @{ roleId = 9001; permissionIds = @() }

    Write-Host ("        结果: success={0} msg={1}" -f $r.Success, $r.Message) -ForegroundColor DarkGray

    (-not $r.Success) -and $r.Message -match '内置'
}

Invoke-Case 'API-ADM-084' 'permissions/Update 可以编辑自定义权限点' {
    $code = 'regtest' + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString()
    $created = Post-Ep '/gateway/permissions/Create' @{
        name = '回归权限'
        code = "$code`:read"
        apiPath = '/gateway/users/List'
        parentId = 2103
        sortOrder = 999
        description = '临时权限'
    }
    if (-not $created.Success) { return $false }

    $updated = Post-Ep '/gateway/permissions/Update' @{
        permissionId = $created.data
        name = '回归权限已修改'
        code = "$code`:read"
        apiPath = '/gateway/users/List'
        sortOrder = 998
        description = '已修改'
    }
    $deleted = Post-Ep '/gateway/permissions/Delete' @{ permissionId = $created.data }
    $updated.Success -and $deleted.Success
}

Invoke-Case 'API-ADM-085' 'admin/orders/Cancel 不存在的订单返回 404' {
    $r = Post-Ep '/gateway/admin/orders/Cancel' @{ orderNo = 'NO_SUCH_ORDER_FOR_REGRESSION' }
    (-not $r.Success) -and $r.Code -eq 404
}

Invoke-Case 'API-ADM-086' '客户列表返回唯一客户编码' {
    $r = Post-Ep '/gateway/admin/customers/List' @{ page = 1; pageSize = 20 }
    $items = @($r.data.items)
    $r.Success -and $items.Count -gt 0 -and
        @($items | Where-Object { -not $_.customerNo }).Count -eq 0
}

Invoke-Case 'API-ADM-087' '商品列表支持按商户过滤' {
    $all = Invoke-RestMethod "$Gateway/gateway/products/List?page=1&pageSize=100" `
        -Headers $auth -TimeoutSec 25
    $row = @($all.data | Where-Object { [long]$_.merchantId -gt 0 })[0]
    if (-not $row) { return $true }

    $filtered = Invoke-RestMethod "$Gateway/gateway/products/List?page=1&pageSize=100&merchantId=$($row.merchantId)" `
        -Headers $auth -TimeoutSec 25
    $items = @($filtered.data)
    $items.Count -gt 0 -and
        @($items | Where-Object { $_.merchantId -ne $row.merchantId }).Count -eq 0
}

Invoke-Case 'API-ADM-088' '订单列表支持客户、商户、手机号与时间区间筛选' {
    $all = Post-Ep '/gateway/admin/orders/List' @{ page = 1; pageSize = 50 }
    $row = @($all.data.items | Where-Object { [long]$_.merchantId -gt 0 -and $_.receiverPhone })[0]
    if (-not $row) { return $true }

    $byCustomer = Post-Ep '/gateway/admin/orders/List' @{ page = 1; pageSize = 50; customerId = $row.customerId }
    $byMerchant = Post-Ep '/gateway/admin/orders/List' @{ page = 1; pageSize = 50; merchantId = $row.merchantId }
    $byPhone = Post-Ep '/gateway/admin/orders/List' @{ page = 1; pageSize = 50; keyword = $row.receiverPhone }
    $byTime = Post-Ep '/gateway/admin/orders/List' @{
        page = 1; pageSize = 50
        from = '2000-01-01T00:00:00Z'
        to = '2100-01-01T00:00:00Z'
    }

    @($byCustomer.data.items).Count -gt 0 -and
        @($byCustomer.data.items | Where-Object { $_.customerId -ne $row.customerId }).Count -eq 0 -and
        @($byMerchant.data.items | Where-Object { $_.merchantId -ne $row.merchantId }).Count -eq 0 -and
        @($byPhone.data.items | Where-Object { $_.receiverPhone -ne $row.receiverPhone }).Count -eq 0 -and
        @($byTime.data.items).Count -gt 0
}

# 收尾：把临时账号停用。
# 不清理的话每跑一次就在库里多一个启用状态的账号——跑几十次之后
# 账号列表被测试数据淹没，看起来像真的出了问题。
# 项目没有「删除账号」端点（刻意如此，审计要求留痕），所以用停用。
if ($platCreate.Success -and $platCreate.data) {
    try {
        Post-Ep '/gateway/users/UpdateStatus' @{ userId = [long]$platCreate.data; status = 2 } | Out-Null
    }
    catch { }
}
if ($createUser.Success -and $createUser.data) {
    try {
        Post-Ep '/gateway/users/UpdateStatus' @{ userId = [long]$createUser.data; status = 2 } | Out-Null
        Write-Host '  （已停用临时只读账号）' -ForegroundColor DarkGray
    }
    catch {
        Write-Host ('  （停用临时账号失败，不影响结论：' + $_.Exception.Message + '）') -ForegroundColor DarkYellow
    }
}

Write-Host ""
Write-Host ("通过: {0}  失败: {1}" -f $script:pass, $script:fail) -ForegroundColor $(if ($script:fail -gt 0) { 'Red' } else { 'Green' })

if ($script:fail -gt 0) {
    Write-Host "失败用例:" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host ("  - " + $_) -ForegroundColor Red }
    exit 1
}

exit 0
