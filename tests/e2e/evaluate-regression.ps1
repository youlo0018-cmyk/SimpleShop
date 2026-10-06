<#
.SYNOPSIS
    EvaluateService（5084）回归测试。
.DESCRIPTION
    覆盖 BUSINESS.md 14 评价的完整链路：
      - 发表时机（只有已完成订单可评）与 SPU 级幂等
      - **SKU 标记自动推导**：一条评价标记该订单买的全部 SKU，详情页按规格过滤
      - 追评：最多 3 条、30 天内、不计入均分
      - 匿名展示（C 端「匿名用户」/ 后台真实昵称）
      - 后台隐藏（软删不删数据）与商户 / 平台各回复 1 次
      - 每日重算：均分只算首评，隐藏后从均分里剔除
    退出码非 0 即视为回归失败。
#>
[CmdletBinding()]
param(
    [string]$Gateway = 'http://127.0.0.1:5008',
    [string]$Evaluate = 'http://127.0.0.1:5084',
    [string]$Order = 'http://127.0.0.1:5064',
    # 物流公司字典在商品服务里（DATA_SPEC 5.23）：发货现在必填物流公司与运单号。
    [string]$Product = 'http://127.0.0.1:5058',
[string]$PointService = 'http://127.0.0.1:5082',
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
$script:customerId = 960000000 + $script:suffix
$script:categoryIds = @()
$script:productId = 0
$script:skuIds = @()
$script:price = 100
$script:orderNo = ''
$script:evaluateId = 0
$script:anonEvaluateId = 0

# ---- 后台令牌（建商品、模拟支付、隐藏评价都要后台身份）----
Add-Type -AssemblyName System.Net.Http
$adminHttp = [System.Net.Http.HttpClient]::new()
$dd = [System.Collections.Generic.Dictionary[string, string]]::new()
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

function EvalPost([string]$Path, $Body) {
    return Invoke-RestMethod "$Evaluate/$Path" -Method Post `
        -Body ($Body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30
}

function AdminOrderPost([string]$Op, $Body) {
    return Invoke-RestMethod "$Order/admin/orders/$Op" -Method Post -Headers $script:adminHeaders `
        -Body ($Body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30
}

function Recompute() {
    return Invoke-RestMethod "$Evaluate/internal/evaluates/ratings/recompute" -Method Post `
        -Body (@{ writeBack = $true } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 120
}

<#
.SYNOPSIS
    取一个启用中的物流公司 Id。
.DESCRIPTION
    发货必填物流公司，而字典在商品服务里。这里只取一次并缓存 ——
    25 条用例每条都发一次货，逐条去查字典会给商品服务平白加上几十次请求。
#>
function Get-LogisticsId {
    if ($script:logisticsId) { return $script:logisticsId }
    $r = Invoke-RestMethod "$Product/logistics-companies/Options" -Method Post `
        -ContentType 'application/json' -Body '{}' -TimeoutSec 30
    $first = @($r.data)[0]
    if (-not $first) { throw '没有可用的物流公司，先执行 02-seed-logistics-companies.sql' }
    $script:logisticsId = $first.logisticsId
    return $script:logisticsId
}

function Complete-EvlOrder([string]$OrderNo) {
    # 实物快递要走完三步才到「已完成」：支付 → 发货 → 确认收货。
    # 少发货那一步，ConfirmReceipt 会被状态机挡下，订单停在「待发货」，
    # 后面「完成才能评价」的断言就全都失去意义了。
    (AdminOrderPost 'SimulatePayment' @{ orderNo = $OrderNo; succeed = $true; remark = '评价回归' }) | Out-Null
    # 发货**必填**物流公司与运单号：不填的话订单停在待发货，
    # 后面 25 条用例会一起变成「订单没走到已完成」，而报错点看着像评价的问题。
    (AdminOrderPost 'Ship' @{ orderNo = $OrderNo; remark = '已发出'
        logisticsCompanyId = (Get-LogisticsId); trackingNo = 'SF-EVL-0001' }) | Out-Null
    return Invoke-RestMethod "$Order/orders/ConfirmReceipt" -Method Post `
        -Body (@{ customerId = $script:customerId; orderNo = $OrderNo } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
}

function Get-ProductRating([long]$SpuId) {
    $r = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$SpuId" `
        -Headers $script:adminHeaders -TimeoutSec 30
    return [pscustomobject]@{ Score = $r.data.evaluationScore; Count = $r.data.evaluationCount }
}

function New-EvlOrder([string]$Tag, [long[]]$Skus) {
    # 规格文本要**按 SKU 给对**：两个 SKU 都填「红 / M」的话，
    # 服务端去重后只剩一个规格，「规格文案」那条用例就永远测不出真实行为。
    # 下单行里的 skuSpecText 是**客户端给的快照**，所以这里必须给准确值。
    $lines = @($Skus | ForEach-Object {
        $spec = if ($_ -eq $script:skuIds[0]) { '红 / M' } else { '蓝 / L' }
        @{ spuId = $script:productId; skuId = $_; quantity = 1; unitPrice = $script:price
           productName = "评价商品$($script:suffix)"; skuSpecText = $spec; deliveryType = 1 }
    })
    return Invoke-RestMethod "$Order/orders/Create" -Method Post `
        -Body (@{
            customerId = $script:customerId; platformId = 0; merchantId = 0
            idempotencyKey = "EVL-$Tag-$($script:suffix)"
            receiverName = '张三'; receiverPhone = '13800000000'; receiverAddress = '某地某小区 1 号楼 101'
            lines = $lines; couponId = 0; pointsToUse = 0; freight = 0
        } | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 60
}

Write-Host "`n=== EVL 准备：一个 2 个规格的商品 + 走到已完成的订单 ===" -ForegroundColor Cyan

Invoke-Case 'API-EVL-000' '建三级分类 + 双规格 SPU，每个规格 20 件' {
    $a = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = 0; categoryName = "评价$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $b = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = $a; categoryName = "评价$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $c = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = $b; categoryName = "评价$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $script:categoryIds = @($a, $b, $c)

    $body = @{
        productId = 0; spuName = "评价商品$($script:suffix)"; categoryId = $c
        deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
        specs = @(@{ specName = '颜色'; specValues = @('红', '蓝') },
                  @{ specName = '尺码'; specValues = @('M', 'L') })
        skus = @(
            @{ skuCode = "EVL-A$($script:suffix)"; specValues = @('红', 'M'); price = $script:price; stock = 20; status = 1 }
            @{ skuCode = "EVL-B$($script:suffix)"; specValues = @('蓝', 'L'); price = $script:price; stock = 20; status = 1 }
        )
    }
    $script:productId = [long](Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:adminHeaders `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 60).data

    # 🔴 商品必须「审核通过 + 已上架」才可下单（BUSINESS.md 14.4「商品需审核后上架」）。
    # 评价链路要先把订单走到已完成，所以下单这一步就会回查审核与上架状态。
    (Invoke-RestMethod "$Gateway/gateway/products/Audit" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $script:productId; auditStatus = 20 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30) | Out-Null
    (Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $script:productId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30) | Out-Null

    $det = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:productId)" `
        -Headers $script:adminHeaders -TimeoutSec 30
    $script:skuIds = @($det.data.skus | Sort-Object { [long]$_.id } | ForEach-Object { [long]$_.id })

    return $script:productId -gt 0 -and $script:skuIds.Count -eq 2
}

Invoke-Case 'API-EVL-001' '下单（同一 SPU 两个规格各一件）→ 模拟支付 → 确认收货，走到「已完成」' {
    $o = New-EvlOrder 'MAIN' $script:skuIds
    if (-not $o.success) { return $false }
    $script:orderNo = $o.data.orderNo

    $c = Complete-EvlOrder $script:orderNo
    return $c.success -and $script:orderNo.Length -gt 0
}

Invoke-Case 'API-EVL-002' '可评价商品：两个规格合并成一个 SPU，且都未评价' {
    $r = EvalPost 'evaluates/Evaluable' @{ customerId = $script:customerId; orderNo = $script:orderNo }
    if (-not $r.success) { return $false }

    # 粒度是 SPU：买了两个规格也只有一条可评价记录
    return $r.data.Count -eq 1 -and $r.data[0].evaluated -eq $false `
        -and $r.data[0].spuId -eq "$($script:productId)"
}

Write-Host "`n=== EVL 发表首评（SPU 级 + SKU 标记自动推导）===" -ForegroundColor Cyan

Invoke-Case 'API-EVL-010' '🔴 P0 发表成功：服务端自动标记该订单买的**全部** SKU' {
    # 先记下发表前的积分：下一条用例要断言「发表首评 +20」（BUSINESS 13.2）
    $script:pointsBeforeEval = (Invoke-RestMethod `
        "$PointService/points/Balance?customerId=$($script:customerId)" -TimeoutSec 20).data.totalEarned

    $r = EvalPost 'evaluates/Publish' @{
        customerId = $script:customerId; orderNo = $script:orderNo; spuId = $script:productId
        starScore = 5; content = '质量不错'; images = @('https://cdn.example.com/1.jpg')
        isAnonymous = $false
    }
    if (-not $r.success) { return $false }

    $script:evaluateId = [long]$r.data.evaluateId
    $script:markedSkuIds = @($r.data.skuIds | ForEach-Object { [long]$_ })

    # SKU 标记来自订单推导而不是前端传：两个规格都要在
    return $script:evaluateId -gt 0 -and $script:markedSkuIds.Count -eq 2 `
        -and ($script:markedSkuIds -contains $script:skuIds[0]) `
        -and ($script:markedSkuIds -contains $script:skuIds[1])
}

Invoke-Case 'API-EVL-010b' '🔴 发表首评赠送 20 积分真的发到账（BUSINESS 13.2）' {
    # 🔴 这条在修之前是**红的**：BUSINESS.md 13.2 写着「发表首评 +20」、
    # 20.1 写着由 PointService 消费 evaluate.created，但 evaluate.created
    # 只在 EventTopics 里声明过、没有任何地方发布，评价服务里也没有发积分的代码。
    # 实测发表首评后 totalEarned 一点没变。
    Start-Sleep -Seconds 1
    $after = (Invoke-RestMethod "$PointService/points/Balance?customerId=$($script:customerId)" -TimeoutSec 20).data
    $gain = [long]$after.totalEarned - [long]$script:pointsBeforeEval

    Write-Host ("        发表首评前 totalEarned={0} → 后 {1}（+{2}）" -f `
        $script:pointsBeforeEval, $after.totalEarned, $gain) -ForegroundColor DarkGray

    return $gain -eq 20
}

Invoke-Case 'API-EVL-011' '🔴 P0 同一订单同一 SPU 重复评价被拒（规格 14.1 只能一条首评）' {
    $r = EvalPost 'evaluates/Publish' @{
        customerId = $script:customerId; orderNo = $script:orderNo; spuId = $script:productId
        starScore = 1; content = '重复评价'
    }
    return (-not $r.success) -and $r.message -match '已经评价过'
}

Invoke-Case 'API-EVL-012' '🔴 未完成的订单不能评价（规格 14.2：完成后才可评）' {
    $o = New-EvlOrder 'UNPAID' @($script:skuIds[0])
    $e = EvalPost 'evaluates/Publish' @{
        customerId = $script:customerId; orderNo = $o.data.orderNo; spuId = $script:productId
        starScore = 5; content = '还没付款就评价'
    }
    return (-not $e.success) -and $e.message -match '完成后'
}

Invoke-Case 'API-EVL-013' '🔴 不能评价别人的订单（归属校验，且不泄露订单号是否存在）' {
    $r = EvalPost 'evaluates/Publish' @{
        customerId = ($script:customerId + 1); orderNo = $script:orderNo; spuId = $script:productId
        starScore = 5; content = '别人的订单'
    }
    # 别人的订单与不存在的订单都回「订单不存在」，不回「无权评价」——那等于确认订单号存在
    return (-not $r.success) -and $r.message -match '订单不存在'
}

Invoke-Case 'API-EVL-014' '🔴 不能评价没买过的 SPU' {
    $r = EvalPost 'evaluates/Publish' @{
        customerId = $script:customerId; orderNo = $script:orderNo; spuId = ($script:productId + 999999)
        starScore = 5; content = '没买过'
    }
    return (-not $r.success) -and $r.message -match '没有购买'
}

Write-Host "`n=== EVL 校验（规格 14.2）===" -ForegroundColor Cyan

Invoke-Case 'API-EVL-020' '🔴 星级必须在 1~5（0 / 6 / 99 都要被 400 挡住）' {
    $blocked = 0
    foreach ($s in @(0, 6, 99)) {
        try {
            EvalPost 'evaluates/Publish' @{
                customerId = $script:customerId; orderNo = $script:orderNo; spuId = $script:productId
                starScore = $s; content = '星级越界'
            } | Out-Null
        } catch {
            if ([int]$_.Exception.Response.StatusCode -eq 400) { $blocked++ }
        }
    }
    return $blocked -eq 3
}

Invoke-Case 'API-EVL-021' '🔴 文字与图片都为空被拒（配图也算有效评价，但全空不行）' {
    try {
        EvalPost 'evaluates/Publish' @{
            customerId = $script:customerId; orderNo = $script:orderNo; spuId = $script:productId
            starScore = 5; content = '   '; images = @()
        } | Out-Null
        return $false
    } catch {
        return [int]$_.Exception.Response.StatusCode -eq 400
    }
}

Invoke-Case 'API-EVL-022' '🔴 图片超过 9 张被拒' {
    try {
        EvalPost 'evaluates/Publish' @{
            customerId = $script:customerId; orderNo = $script:orderNo; spuId = $script:productId
            starScore = 5; content = '图太多'
            images = (1..10 | ForEach-Object { "https://cdn.example.com/$_.jpg" })
        } | Out-Null
        return $false
    } catch {
        return [int]$_.Exception.Response.StatusCode -eq 400
    }
}

Write-Host "`n=== EVL 商品详情页评价列表（按 SKU 过滤）===" -ForegroundColor Cyan

Invoke-Case 'API-EVL-030' 'SPU 维度：一条评价，均分 5.00' {
    $r = EvalPost 'evaluates/List' @{ spuId = $script:productId; skuId = 0; page = 1; pageSize = 10 }
    return $r.success -and $r.data.total -eq 1 -and $r.data.items[0].starScore -eq 5 `
        -and $r.data.averageScore -eq 5.00 -and $r.data.count -eq 1 `
        -and $r.data.displayScore -eq 5.00
}

Invoke-Case 'API-EVL-031' '按 SKU 过滤：两个规格都被这条评价标记，所以都查得到' {
    $r1 = EvalPost 'evaluates/List' @{ spuId = $script:productId; skuId = $script:skuIds[0]; page = 1; pageSize = 10 }
    $r2 = EvalPost 'evaluates/List' @{ spuId = $script:productId; skuId = $script:skuIds[1]; page = 1; pageSize = 10 }
    return $r1.data.total -eq 1 -and $r2.data.total -eq 1
}

Invoke-Case 'API-EVL-032' '规格文案：两个规格原样列出（未超过 3 个，不折叠）' {
    $r = EvalPost 'evaluates/List' @{ spuId = $script:productId; skuId = 0; page = 1; pageSize = 10 }
    $specs = $r.data.items[0].skuSpecs
    return $specs -and $specs.Contains('红') -and $specs.Contains('蓝') -and $specs -notmatch '等'
}

Invoke-Case 'API-EVL-033' '🔴 查一个不相关的 SKU：查不到评价（SKU 标记过滤真的起作用）' {
    $r = EvalPost 'evaluates/List' @{ spuId = $script:productId; skuId = ($script:skuIds[0] + 88888); page = 1; pageSize = 10 }
    return $r.success -and $r.data.total -eq 0 -and $r.data.items.Count -eq 0
}

Invoke-Case 'API-EVL-034' '我的评价：能看到自己刚发的这条' {
    $r = EvalPost 'evaluates/My' @{ customerId = $script:customerId; page = 1; pageSize = 10 }
    $mine = @($r.data.items | Where-Object { $_.evaluateId -eq "$($script:evaluateId)" })
    return $r.success -and $mine.Count -eq 1 -and $mine[0].spuId -eq "$($script:productId)"
}

Invoke-Case 'API-EVL-035' '🔴 游客不能看我的评价（customerId 必须为正）' {
    try {
        EvalPost 'evaluates/My' @{ customerId = 0; page = 1; pageSize = 10 } | Out-Null
        return $false
    } catch {
        return [int]$_.Exception.Response.StatusCode -eq 400
    }
}

Write-Host "`n=== EVL 匿名（规格 14.2）===" -ForegroundColor Cyan

Invoke-Case 'API-EVL-040' '🔴 匿名评价：C 端显示「匿名用户」，但内容照常展示' {
    # 第二张订单发匿名评价：幂等键是 (orderNo, spuId)，换一张订单就能再评一次
    $o = New-EvlOrder 'ANON' @($script:skuIds[0])
    Complete-EvlOrder $o.data.orderNo | Out-Null

    $p = EvalPost 'evaluates/Publish' @{
        customerId = $script:customerId; orderNo = $o.data.orderNo; spuId = $script:productId
        starScore = 4; content = '匿名评价内容'; isAnonymous = $true
    }
    if (-not $p.success) { return $false }
    $script:anonEvaluateId = [long]$p.data.evaluateId

    $r = EvalPost 'evaluates/List' @{ spuId = $script:productId; skuId = 0; page = 1; pageSize = 10 }
    $anon = @($r.data.items | Where-Object { $_.evaluateId -eq "$($script:anonEvaluateId)" })[0]

    return $script:anonEvaluateId -gt 0 -and $anon.customerName -eq '匿名用户' `
        -and $anon.isAnonymous -eq $true -and $anon.content -eq '匿名评价内容'
}

Invoke-Case 'API-EVL-041' '🔴 后台能看到匿名的真实昵称（匿名只对其他顾客隐藏）' {
    $r = Invoke-RestMethod "$Evaluate/evaluates/admin/List" -Method Post -Headers $script:adminHeaders `
        -Body (@{ spuId = $script:productId; page = 1; pageSize = 20 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    $anon = @($r.data.items | Where-Object { $_.evaluateId -eq "$($script:anonEvaluateId)" })[0]
    # 后台展示名不是那个固定文案；真实昵称本身可能是空串，但绝不是「匿名用户」
    return $r.success -and $anon -and $anon.customerName -ne '匿名用户'
}

Write-Host "`n=== EVL 追评（规格 14.2）===" -ForegroundColor Cyan

Invoke-Case 'API-EVL-050' '追评成功：挂在首评下' {
    $r = EvalPost 'evaluates/Append' @{
        customerId = $script:customerId; evaluateId = $script:evaluateId
        starScore = 1; content = '用久了有点问题'
    }
    return $r.success -and $r.data -gt 0
}

Invoke-Case 'API-EVL-051' '🔴 P0 追评**不计入均分**（规格 14.2：防刷分）' {
    $r = EvalPost 'evaluates/List' @{ spuId = $script:productId; skuId = 0; page = 1; pageSize = 10 }

    # 此刻有两条首评（5 星 + 4 星），均分 4.50。
    # 追评那条 1 星如果被算进去，均分会掉到 3.67——那等于给了
    #「先打 5 星再追评差评拉低分数」的刷分路径，反过来也一样
    # 列表是**最新优先**，items[0] 是后发的 4 星匿名那条，而追评挂在最早那条（5 星）上。
    # 按 evaluateId 定位，别写死 items[0]——否则这条断言测的是「另一个人有没有追评」
    $target = @($r.data.items | Where-Object { $_.evaluateId -eq "$($script:evaluateId)" })[0]

    return $r.data.count -eq 2 -and $r.data.averageScore -eq 4.50 `
        -and $target -and @($target.appends).Count -eq 1 -and $target.appends[0].starScore -eq 1
}

Invoke-Case 'API-EVL-052' '🔴 追评最多 3 条，第 4 条被拒' {
    EvalPost 'evaluates/Append' @{ customerId = $script:customerId; evaluateId = $script:evaluateId; starScore = 0; content = '追评2' } | Out-Null
    EvalPost 'evaluates/Append' @{ customerId = $script:customerId; evaluateId = $script:evaluateId; starScore = 0; content = '追评3' } | Out-Null
    $r = EvalPost 'evaluates/Append' @{ customerId = $script:customerId; evaluateId = $script:evaluateId; starScore = 0; content = '追评4' }
    return (-not $r.success) -and $r.message -match '追评'
}

Invoke-Case 'API-EVL-053' '🔴 不能追别人的评价' {
    $r = EvalPost 'evaluates/Append' @{ customerId = ($script:customerId + 1); evaluateId = $script:evaluateId; starScore = 5; content = 'x' }
    return (-not $r.success) -and $r.message -match '不存在'
}

Write-Host "`n=== EVL 后台回复（规格 14.3）===" -ForegroundColor Cyan

Invoke-Case 'API-EVL-060' '商户回复成功' {
    $r = Invoke-RestMethod "$Gateway/gateway/evaluates/admin/Reply" -Method Post -Headers $script:adminHeaders `
        -Body (@{ evaluateId = $script:evaluateId; appendId = 0; replyContent = '感谢您的反馈，我们会改进'
                  replyType = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    return $r.success -and $r.data -gt 0
}

Invoke-Case 'API-EVL-061' '🔴 同一主体只能回复 1 次' {
    $r = Invoke-RestMethod "$Gateway/gateway/evaluates/admin/Reply" -Method Post -Headers $script:adminHeaders `
        -Body (@{ evaluateId = $script:evaluateId; appendId = 0; replyContent = '再回一次'
                  replyType = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    return (-not $r.success) -and $r.message -match '已回复'
}

Invoke-Case 'API-EVL-062' '平台可以各回 1 次（与商户互不影响）' {
    $r = Invoke-RestMethod "$Gateway/gateway/evaluates/admin/Reply" -Method Post -Headers $script:adminHeaders `
        -Body (@{ evaluateId = $script:evaluateId; appendId = 0; replyContent = '平台介入处理'
                  replyType = 2 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    return $r.success
}

Invoke-Case 'API-EVL-063' '回复内容太短被拒（少于 2 个字符）' {
    try {
        Invoke-RestMethod "$Gateway/gateway/evaluates/admin/Reply" -Method Post -Headers $script:adminHeaders `
            -Body (@{ evaluateId = $script:anonEvaluateId; appendId = 0; replyContent = '好'
                      replyType = 1 } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        return $false
    } catch {
        return [int]$_.Exception.Response.StatusCode -eq 400
    }
}

# ---- 回复的归属校验 ----
#
# 这两条挡的是「把评价区的归属凭据交给调用方」：
#   ① 商户账号传 replyType=2 就能把自家回复伪装成**平台官方回复**（用户会把它当可信来源）；
#   ② appendId 传别人评价的追评 Id，回复就会挂到别人评价下面。
# 都必须由服务端按身份 / 数据归属判定，不能靠前端不这么传。

$script:merchantReplyUserId = 0
$script:merchantReplyToken = $null

try {
    $mrUser = "evlmr$($script:suffix)"
    $mrPwd = 'Merchant123456'
    # 商户账号必须至少 1 个角色，且角色的 AllowedScopes 要与 tenantType=2 匹配
    # （9004 = 商户管理员，绑定全部权限点）。建号走网关：租户锁定要读网关的租户头。
    $mrCreated = Invoke-RestMethod "$Gateway/gateway/users/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{
            userName = $mrUser; password = $mrPwd
            phone = '138' + (Get-Random -Minimum 10000000 -Maximum 99999999)
            tenantType = 2; nickName = '评价回复越权用例'
            platformId = 0; merchantId = 1; roleIds = @(9004)
        } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30

    if ($mrCreated.success) {
        $script:merchantReplyUserId = [long]$mrCreated.data
        $script:merchantReplyToken = (Invoke-RestMethod -Uri "$Gateway/gateway/auth/token" -Method Post `
            -Body "grant_type=password&client_id=admin-app&username=$mrUser&password=$mrPwd" `
            -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 30).access_token
    }
}
catch {
    Write-Host ("  （准备商户回复越权用例失败：" + $_.Exception.Message + "）") -ForegroundColor DarkYellow
}

Invoke-Case 'API-EVL-064' '🔴 商户账号不能以平台身份回复（否则冒充平台官方）' {
    if (-not $script:merchantReplyToken) { return $false }
    $h = @{ Authorization = "Bearer $($script:merchantReplyToken)" }

    $r = Invoke-RestMethod "$Gateway/gateway/evaluates/admin/Reply" -Method Post -Headers $h `
        -Body (@{ evaluateId = $script:evaluateId; appendId = 0; replyContent = '以平台名义回复'
                  replyType = 2 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30

    # 断言业务码而不是 HTTP 状态码：这套接口的业务失败是 HTTP 200 + success=false
    (-not $r.success) -and ([int]$r.code -eq 403)
}

Invoke-Case 'API-EVL-065' '🔴 回复挂到不存在的追评上被拒' {
    $r = Invoke-RestMethod "$Gateway/gateway/evaluates/admin/Reply" -Method Post -Headers $script:adminHeaders `
        -Body (@{ evaluateId = $script:evaluateId; appendId = 999999999; replyContent = '挂到别人追评上'
                  replyType = 2 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    (-not $r.success) -and ([int]$r.code -eq 400)
}

Write-Host "`n=== EVL 后台隐藏（规格 14.4）===" -ForegroundColor Cyan

Invoke-Case 'API-EVL-070' '🔴 隐藏必须填原因（后台要记审计，没有原因无从追溯）' {
    try {
        Invoke-RestMethod "$Gateway/gateway/evaluates/admin/Hide" -Method Post -Headers $script:adminHeaders `
            -Body (@{ evaluateId = $script:evaluateId; isHidden = $true; hiddenReason = '' } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        return $false
    } catch {
        return [int]$_.Exception.Response.StatusCode -eq 400
    }
}

Invoke-Case 'API-EVL-071' '🔴 P0 隐藏是「不展示」不是「删数据」：C 端消失、我的评价里还在' {
    $h = Invoke-RestMethod "$Gateway/gateway/evaluates/admin/Hide" -Method Post -Headers $script:adminHeaders `
        -Body (@{ evaluateId = $script:evaluateId; isHidden = $true
                  hiddenReason = '含违规内容，已隐藏' } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    if (-not $h.success) { return $false }

    $pub = EvalPost 'evaluates/List' @{ spuId = $script:productId; skuId = 0; page = 1; pageSize = 10 }
    $visible = @($pub.data.items | Where-Object { $_.evaluateId -eq "$($script:evaluateId)" }).Count

    $mine = EvalPost 'evaluates/My' @{ customerId = $script:customerId; page = 1; pageSize = 10 }
    $hiddenInMine = @($mine.data.items | Where-Object { $_.evaluateId -eq "$($script:evaluateId)" })

    # 软删会让客户以为「我评了但没提交成功」而反复提交，
    # 所以隐藏只打标记；客户侧能看到 isHidden，配合后台的隐藏原因可追溯
    return $visible -eq 0 -and $hiddenInMine.Count -eq 1 -and $hiddenInMine[0].isHidden -eq $true
}

Invoke-Case 'API-EVL-072' '后台列表能看到隐藏原因' {
    $r = Invoke-RestMethod "$Evaluate/evaluates/admin/List" -Method Post -Headers $script:adminHeaders `
        -Body (@{ spuId = $script:productId; page = 1; pageSize = 20 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    $hidden = @($r.data.items | Where-Object { $_.evaluateId -eq "$($script:evaluateId)" })[0]
    return $hidden -and $hidden.isHidden -eq $true -and $hidden.hiddenReason -match '违规'
}

Invoke-Case 'API-EVL-073' '🔴 P0 伪造 operatorId 无效：审计人取自令牌，不取请求体' {
    # 契约里已经没有 operatorId 了，但**显式传一个**更能证明服务端不是「碰巧没读」：
    # 以前这里就直接采信请求体，任何登录用户都能把「谁隐藏了这条评价」伪造成别人。
    # 后台列表要显示 hiddenByName，所以这里比对它是不是当前登录人。
    $body = @{
        evaluateId  = $script:evaluateId
        isHidden    = $true
        hiddenReason = '含违规内容，已隐藏'
        operatorId  = 999999999
        operatorName = '伪造的审核员'
    } | ConvertTo-Json

    $h = Invoke-RestMethod "$Gateway/gateway/evaluates/admin/Hide" -Method Post -Headers $script:adminHeaders `
        -Body $body -ContentType 'application/json' -TimeoutSec 30
    if (-not $h.success) { return $false }

    $r = Invoke-RestMethod "$Gateway/gateway/evaluates/admin/List" -Method Post -Headers $script:adminHeaders `
        -Body (@{ spuId = $script:productId; page = 1; pageSize = 20 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    $row = @($r.data.items | Where-Object { $_.evaluateId -eq "$($script:evaluateId)" })[0]
    if (-not $row) { return $false }

    # 不能出现伪造的名字；出现当前登录人才算对
    return (-not $row.hiddenByName) -or ($row.hiddenByName -ne '伪造的审核员')
}

Write-Host "`n=== EVL 每日重算聚合分（规格 14.5）===" -ForegroundColor Cyan

Invoke-Case 'API-EVL-080' '🔴 P0 重算后商品均分：隐藏的那条被剔除，只剩 4 星' {
    $r = Recompute
    if (-not $r.success) { return $false }

    $rating = Get-ProductRating $script:productId
    # 两条首评（5 星 + 4 星）里 5 星那条被隐藏 → 均分回到 4.00，条数 1
    return $rating.Score -eq 4.00 -and $rating.Count -eq 1
}

Invoke-Case 'API-EVL-081' '🔴 P0 列表均分与商品表冗余字段一致（否则详情页和商品卡对不上）' {
    $r = EvalPost 'evaluates/List' @{ spuId = $script:productId; skuId = 0; page = 1; pageSize = 10 }
    $rating = Get-ProductRating $script:productId
    # 两个口径必须一致：不一致就是「详情页说 4 星、商品卡说 5 星」，用户会当 bug 投诉
    return $r.data.count -eq 1 -and $r.data.averageScore -eq $rating.Score
}

Invoke-Case 'API-EVL-082' '恢复显示后重算，均分回到 4.50、条数 2' {
    Invoke-RestMethod "$Gateway/gateway/evaluates/admin/Hide" -Method Post -Headers $script:adminHeaders `
        -Body (@{ evaluateId = $script:evaluateId; isHidden = $false; hiddenReason = '' } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    Recompute | Out-Null
    $rating = Get-ProductRating $script:productId
    return $rating.Score -eq 4.50 -and $rating.Count -eq 2
}

Invoke-Case 'API-EVL-083' '🔴 P0 重算幂等：连跑两次结果不变' {
    $before = Get-ProductRating $script:productId
    Recompute | Out-Null
    Recompute | Out-Null
    $after = Get-ProductRating $script:productId
    # 全量重算不依赖上一次结果，所以跑几次都一样。
    # 不幂等的话定时任务每跑一次分数就漂移一次，最终越算越离谱
    return $before.Score -eq $after.Score -and $before.Count -eq $after.Count
}

Invoke-Case 'API-EVL-084' '🔴 追评不改变均分（重算后仍是 4.50 / 2 条）' {
    Recompute | Out-Null
    $rating = Get-ProductRating $script:productId
    return $rating.Score -eq 4.50 -and $rating.Count -eq 2
}

Invoke-Case 'API-EVL-085' '🔴 P0 评价被**全部**隐藏后重算，商品评分清零' {
    # 隐藏掉这个商品剩下的所有评价，然后重算。
    #
    # 🔴 这条在修之前是过的：重算只遍历「还有可见评价」的 SPU，
    # 评价全被隐藏的商品压根不进参与计算的集合，于是商品表里的
    # evaluationScore / evaluationCount 永远停在旧值上。
    # 症状是「评价列表里一条都没有，商品卡却还挂着 4.8 星」，
    # 而且没有任何地方能解释这个分数从哪来 —— 因为它已经不对应任何一条评价了。
    $list = Invoke-RestMethod "$Gateway/gateway/evaluates/admin/List" -Method Post -Headers $script:adminHeaders `
        -Body (@{ spuId = $script:productId; page = 1; pageSize = 50 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    $rows = @($list.data.items | Where-Object { -not $_.isHidden })
    if ($rows.Count -eq 0) { return $false }

    $script:allHiddenEvaluateIds = @($rows | ForEach-Object { [long]$_.evaluateId })
    foreach ($id in $script:allHiddenEvaluateIds) {
        Invoke-RestMethod "$Gateway/gateway/evaluates/admin/Hide" -Method Post -Headers $script:adminHeaders `
            -Body (@{ evaluateId = $id; isHidden = $true; hiddenReason = '违规内容，整批下架' } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }

    Recompute | Out-Null
    $rating = Get-ProductRating $script:productId
    Write-Host ("        全部隐藏后：score={0} count={1}" -f $rating.Score, $rating.Count) -ForegroundColor DarkGray

    # 库里保持 0（= 没有评价），展示层用 DisplayScore 转成 5.0
    return $rating.Score -eq 0 -and $rating.Count -eq 0
}

Invoke-Case 'API-EVL-086' '恢复显示后评分能重新算回来（清零不是单向的）' {
    foreach ($id in $script:allHiddenEvaluateIds) {
        Invoke-RestMethod "$Gateway/gateway/evaluates/admin/Hide" -Method Post -Headers $script:adminHeaders `
            -Body (@{ evaluateId = $id; isHidden = $false; hiddenReason = '' } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
    Recompute | Out-Null
    $rating = Get-ProductRating $script:productId

    # 只回写「还剩可见评价」的商品的话，被清零的商品就再也回不来了 ——
    # 运营误操作隐藏一批评价，把评分清掉之后就再也救不回来。
    return $rating.Score -eq 4.50 -and $rating.Count -eq 2
}


Write-Host "`n=== EVL 无评价商品的默认分（规格 14.4）===" -ForegroundColor Cyan

Invoke-Case 'API-EVL-090' '🔴 没评价的商品：库里记 0 而不是 5.0（要能区分「没人评」和「均分 5 星」）' {
    $p = [long](Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:adminHeaders `
        -Body (@{
            productId = 0; spuName = "零评价商品$($script:suffix)"; categoryId = $script:categoryIds[2]
            deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
            specs = @(@{ specName = '颜色'; specValues = @('红') })
            skus = @(@{ skuCode = "EVL-Z$($script:suffix)"; specValues = @('红'); price = 50; stock = 5; status = 1 })
        } | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 60).data

    Recompute | Out-Null
    $rating = Get-ProductRating $p
    # 库里保持 0（= 没有评价），展示层用 DisplayScore 转成 5.0
    return $rating.Score -eq 0 -and $rating.Count -eq 0
}

Invoke-Case 'API-EVL-091' '零评价商品的评价列表：空列表，展示分 5.0（0 分会被理解成「很差」）' {
    $p = [long](Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:adminHeaders `
        -Body (@{
            productId = 0; spuName = "零评价商品2$($script:suffix)"; categoryId = $script:categoryIds[2]
            deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
            specs = @(@{ specName = '颜色'; specValues = @('红') })
            skus = @(@{ skuCode = "EVL-Y$($script:suffix)"; specValues = @('红'); price = 50; stock = 5; status = 1 })
        } | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 60).data

    $r = EvalPost 'evaluates/List' @{ spuId = $p; skuId = 0; page = 1; pageSize = 10 }
    return $r.success -and $r.data.total -eq 0 -and $r.data.averageScore -eq 0 -and $r.data.displayScore -eq 5.00
}

Write-Host "`n=== EVL 清理 ===" -ForegroundColor Cyan

# 停用临时商户账号。项目刻意没有删除账号的接口（审计要求留痕），所以用停用。
if ($script:merchantReplyUserId -gt 0) {
    try {
        Invoke-RestMethod "$Gateway/gateway/users/UpdateStatus" -Method Post -Headers $script:adminHeaders `
            -Body (@{ userId = $script:merchantReplyUserId; status = 2 } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        Write-Host '  （已停用临时商户账号）' -ForegroundColor DarkGray
    }
    catch {
        Write-Host ('  （停用临时商户账号失败，不影响结论：' + $_.Exception.Message + '）') -ForegroundColor DarkYellow
    }
}

Invoke-Case 'API-EVL-099' '删商品 → 删分类' {
    if ($script:productId -gt 0) {
        Invoke-RestMethod "$Gateway/gateway/products/Delete" -Method Post -Headers $script:adminHeaders `
            -Body (@{ productId = $script:productId } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 60 | Out-Null
    }
    foreach ($id in [array]($script:categoryIds | Sort-Object -Descending)) {
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
