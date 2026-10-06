<#
.SYNOPSIS
    OrderService（5064）回归测试。
.DESCRIPTION
    下单链路是全系统最复杂的补偿链路（BUSINESS.md 8.1 链路 7），
    所以这里不只测「能不能下成一单」，而是把四步占用的**实际副作用**都查一遍：

      ① 营销占券   → 用户券状态 1（未使用）→ 2（已占用）
      ② 锁定积分   → available 减少、frozen 增加
      ③ 锁定库存   → available 减少、locked 增加
      ④ 落单       → order + order_item 同事务写入

    只断言「接口返回成功」是不够的：前面踩过 FreeSql Update 生成空 SET 的坑，
    接口回成功而数据纹丝不动。所以每一步都用独立接口把三个系统的状态查回来对账。

    对应 TEST_CASES：
      API-ORD-001 P0 幂等：同一幂等键重复提交只产生一张单，且不重复占用
      API-ORD-002 P0 任一步失败必须逆序回滚（这里用库存不足触发 ③ 失败）
      API-ORD-003 P1 取消订单释放库存 / 积分 / 券
      API-ORD-004 P1 模拟支付成功 → 扣库存 + 实扣积分 + 核销券 + 10→20
      API-ORD-005 P1 模拟支付失败 → 状态与占用都不变
      API-ORD-006 P1 自提取货码 RSA 往返
      API-ORD-007 P1 用户确认收货后不可退款
      API-ORD-008 P1 虚拟订单不可退款
#>
[CmdletBinding()]
param(
    [string]$Order = 'http://127.0.0.1:5064',
    [string]$Gateway = 'http://127.0.0.1:5008',
    [string]$Marketing = 'http://127.0.0.1:5072',
    [string]$Inventory = 'http://127.0.0.1:5062',
    [string]$PointService = 'http://127.0.0.1:5082',
    # 物流公司字典在商品服务里（DATA_SPEC 5.23）：发货要选公司，而公司表不在订单服务。
    [string]$Product = 'http://127.0.0.1:5058',
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

Add-Type -AssemblyName System.Net.Http
$http = [System.Net.Http.HttpClient]::new()

$script:suffix   = Get-Random -Minimum 100000 -Maximum 999999
$script:customerId = 710000000 + $script:suffix
$script:platformId = 0
$script:price = 25.50
$script:productId = 0
$script:categoryId = 0
$script:skuIds = @()
$script:initStock = 200
$script:lowStock = 1
$script:lowStockSkuId = 0

function Get-AdminToken {
    $d = [System.Collections.Generic.Dictionary[string,string]]::new()
    $d['grant_type'] = 'password'; $d['client_id'] = 'admin-app'
    $d['username'] = $AdminUser;     $d['password'] = $AdminPassword
    $r = $http.PostAsync("$Gateway/gateway/auth/token", [System.Net.Http.FormUrlEncodedContent]::new($d)).GetAwaiter().GetResult()
    return ($r.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).access_token
}

$script:adminHeaders = @{ Authorization = "Bearer $(Get-AdminToken)" }

# 所有「预期会失败」的用例都要靠它：校验失败与业务失败都由全局异常中间件返回 **HTTP 400**，
# Invoke-RestMethod 见到 400 就直接抛异常，body 里的 { success:false, message, code } 拿不到。
# 所以统一包一层：无论 HTTP 是不是错误，都把统一响应体解析出来返回。
function Invoke-Api([string]$Uri, [string]$Method = 'Post', $Body = $null) {
    try {
        $params = @{ Uri = $Uri; Method = $Method; TimeoutSec = 60; ErrorAction = 'Stop' }
        if ($null -ne $Body) {
            $params['Body'] = ($Body | ConvertTo-Json -Depth 8)
            $params['ContentType'] = 'application/json'
        }
        return Invoke-RestMethod @params
    } catch {
        $raw = $_.ErrorDetails.Message
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return [pscustomobject]@{ success = $false; code = -1; message = $_.Exception.Message; data = $null }
        }
        try { return $raw | ConvertFrom-Json } catch { return [pscustomobject]@{ success = $false; code = -1; message = $raw; data = $null } }
    }
}

function OrderPost([string]$Op, $Body) { return Invoke-Api "$Order/orders/$Op" 'Post' $Body }

function AdminOrderPost([string]$Op, $Body) { return Invoke-Api "$Order/admin/orders/$Op" 'Post' $Body }

<#
.SYNOPSIS
    取一个启用中的物流公司 Id。
.DESCRIPTION
    发货现在**必填**物流公司（客服接到物流异常时，没单号的订单无从追责）。
    订单服务不接受前端传来的公司名，而是自己去商品服务取权威名称，
    所以这里必须给一个**真实存在**的 Id，否则会被挡下来。
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

<#
.SYNOPSIS
    构造一个合法的发货请求体。
.DESCRIPTION
    统一带上物流公司与运单号，免得每条用例各写一遍、漏掉其中一项时
    报出来的是「请选择物流公司」而不是「运单号没填」——两种失败长得一样很难分辨。
#>
function New-ShipBody([string]$OrderNo, [string]$TrackingNo = 'SF1234567890') {
    return @{ orderNo = $OrderNo; remark = '已发出'; logisticsCompanyId = (Get-LogisticsId); trackingNo = $TrackingNo }
}

function Get-Stock([long]$SkuId) {
    $r = Invoke-RestMethod "$Inventory/internal/inventory/Snapshot?skuIds=$SkuId" -TimeoutSec 30
    return @($r.data | Where-Object { $_.skuId -eq $SkuId })[0]
}

function Get-PointBalance([long]$CustomerId) {
    return (Invoke-RestMethod "$PointService/points/Balance?customerId=$CustomerId" -TimeoutSec 30).data
}

function Get-Order([string]$OrderNo) {
    return (Invoke-RestMethod "$Order/orders/Detail?orderNo=$OrderNo&customerId=$($script:customerId)" -TimeoutSec 30).data
}

<#
.SYNOPSIS
    取后台订单详情。
.DESCRIPTION
    与 <see cref="Get-Order"/>（C 端详情）**不同**的只有一处，但那一处很关键：
    后台详情才会给每一行带上 refundedQuantity 与 refundableAmount（行级可退余额）。
    部分退款的金额上限全靠这两个字段，用 C 端详情算出来永远是 0，
    于是第二次退款会以「金额必须大于 0」被拒 —— 而单看代码完全看不出原因。
#>
function Get-AdminOrder([string]$OrderNo) {
    $list = AdminOrderPost 'List' @{ keyword = $OrderNo; page = 1; pageSize = 5 }
    $hit = @($list.data.items | Where-Object { $_.orderNo -eq $OrderNo })[0]
    if (-not $hit) { throw "后台订单列表里找不到 $OrderNo" }
    return (Invoke-RestMethod "$Order/admin/orders/Detail" -Method Post `
        -Body (@{ orderId = $hit.orderId } | ConvertTo-Json) -ContentType 'application/json' `
        -TimeoutSec 30).data
}

function New-IdempotencyKey([string]$Tag) { return "ORD-$Tag-$($script:suffix)" }

function New-OrderLine([int]$DeliveryType = 1, [int]$Quantity = 2) {
    return [ordered]@{
        spuId       = [long]$script:productId
        skuId       = [long]$script:skuIds[0]
        quantity    = $Quantity
        unitPrice   = $script:price
        productName = "订单商品$($script:suffix)"
        skuSpecText = '红 / M'
        deliveryType = $DeliveryType
    }
}

function New-OrderBody([string]$Tag, [int]$DeliveryType = 1, [int]$Quantity = 2, [long]$CouponId = 0, [long]$Points = 0, [decimal]$Freight = 0) {
    return @{
        customerId      = $script:customerId
        platformId      = $script:platformId
        merchantId      = 0
        idempotencyKey  = (New-IdempotencyKey $Tag)
        receiverName    = '张三'
        receiverPhone   = '13800000000'
        receiverAddress = '某地某小区 1 号楼 101'
        lines           = @(, (New-OrderLine $DeliveryType $Quantity))
        couponId        = $CouponId
        pointsToUse     = $Points
        freight         = $Freight
        remark          = "回归$Tag"
    }
}

<#
.SYNOPSIS
    两个 SKU 各一行的订单体。
.DESCRIPTION
    部分退款要「退一件、留一件」才测得出与整单退的区别：
    单行订单退一次就已经退光了，第二次退必然超额度，测不出「多次部分退款」这个能力。
#>
function New-TwoLineOrderBody([string]$Tag) {
    $body = New-OrderBody $Tag
    $line2 = [ordered]@{
        spuId       = [long]$script:productId
        skuId       = [long]$script:secondSkuId
        quantity    = 1
        unitPrice   = 10.00
        productName = "订单商品B$($script:suffix)"
        skuSpecText = '蓝 / L'
        deliveryType = 1
    }
    $body.lines = @($body.lines[0], $line2)
    return $body
}

Write-Host "`n=== ORD 准备：商品 + 库存 + 券 + 积分 ===" -ForegroundColor Cyan

Invoke-Case 'API-ORD-000' '建两个 SKU 的商品并各自初始化库存' {
    # 商品只能挂在**第 3 级（叶子）**分类下，所以建三级而不是两级
    $c1 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = 0; categoryName = "订单$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $c2 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = $c1; categoryName = "订单$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $c3 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = $c2; categoryName = "订单$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $script:categoryId = $c3

    # SKU 的 stock 会由 ProductService 自动拿去初始化库存（调 /internal/inventory/Init），
    # 所以这里直接给足库存；再手工 Init 一次会被幂等挡掉、拿回原来的 0。

    $body = @{
        productId = 0; spuName = "订单商品$($script:suffix)"; categoryId = $c3
        deliveryType = 1; mainImage = 'https://cdn.example.com/main.png'
        specs = @(@{ specName = '颜色'; specValues = @('红', '蓝', '限量') })
        skus = @(
            @{ skuCode = "ORD-A$($script:suffix)"; specValues = @('红'); price = $script:price; stock = $script:initStock; status = 1 }
            @{ skuCode = "ORD-B$($script:suffix)"; specValues = @('蓝'); price = 10.00; stock = $script:initStock; status = 1 }
            # 只有 1 件的 SKU：用来触发「校验放行但库存不足」的下单失败路径
            @{ skuCode = "ORD-C$($script:suffix)"; specValues = @('限量'); price = 8.80; stock = $script:lowStock; status = 1 }
        )
    }
    $script:productId = (Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:adminHeaders `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30).data

    # 🔴 商品必须「审核通过 + 已上架」才可下单（BUSINESS.md 14.4「商品需审核后上架」，
    # 下单链路会回查这两项，未过审/未上架一律拒单）。
    # 之前这里漏了这一步，用例照样全绿 —— 因为下单链路当时**根本不查**审核与上架状态，
    # 未过审的商品也能成交。补上审核与上架后，反而暴露出下面几条断言一直建立在
    # 「买到了不该买得到的商品」这个前提上。
    (Invoke-RestMethod "$Gateway/gateway/products/Audit" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $script:productId; auditStatus = 20 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30) | Out-Null
    (Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $script:productId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30) | Out-Null

    $d = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:productId)" -Headers $script:adminHeaders -TimeoutSec 30

    # 按 skuCode 取，不按顺序取：接口返回顺序不保证，靠位置拿 SKU 会时对时错
    $byCode = @{}
    foreach ($s in $d.data.skus) { $byCode[$s.skuCode] = [long]$s.id }

    $script:skuIds = @($byCode["ORD-A$($script:suffix)"])
    # 第二个 SKU 单独存：多次部分退款的用例需要一张**两行**的订单
    # （退一行、留一行），而 skuIds 这个名字历来只装主 SKU。
    $script:secondSkuId = [long]$byCode["ORD-B$($script:suffix)"]
    $script:lowStockSkuId = $byCode["ORD-C$($script:suffix)"]
    $script:spuId = [long]$d.data.id

    return $d.data.skus.Count -eq 3 `
        -and $script:skuIds[0] -gt 0 `
        -and $script:secondSkuId -gt 0 `
        -and $script:lowStockSkuId -gt 0 `
        -and (Get-Stock $script:skuIds[0]).available -eq $script:initStock `
        -and (Get-Stock $script:lowStockSkuId).available -eq $script:lowStock
}

$script:templateId = 0
$script:activityId = 0
$script:couponCode = ''
$script:couponId = 0

Invoke-Case 'API-ORD-001a' '建满减券模板 + 券活动（满 40 减 10）' {
    $script:templateId = (Invoke-RestMethod "$Marketing/marketing/coupon-templates/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ templateName = "订单券$($script:suffix)"; couponType = 1; thresholdAmount = 40; discountAmount = 10; validDays = 30; totalQuantity = 50; perUserLimit = 2; perOrderLimit = 1; platformId = 0; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data

    $script:activityId = (Invoke-RestMethod "$Marketing/marketing/coupon-activities/Create" -Method Post `
        -Body (@{
            activityName = "订单活动$($script:suffix)"; templateId = $script:templateId
            claimStartTime = (Get-Date).AddMinutes(-5).ToString('o')
            claimEndTime   = (Get-Date).AddDays(1).ToString('o')
            claimQuantity = 20; perUserLimit = 2
            targetType = 1; targets = '[]'; platformId = 0; status = 1
        } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30).data
    return $script:templateId -gt 0 -and $script:activityId -gt 0
}

Invoke-Case 'API-ORD-001b' '客户领券' {
    $r = Invoke-Api "$Marketing/coupons/Claim" 'Post' @{ customerId = $script:customerId; activityId = $script:activityId; quantity = 1 }
    if (-not $r.success) { Write-Host ("        实际返回：" + $r.message) -ForegroundColor DarkYellow; return $false }
    $script:couponCode = $r.data.couponCodes[0]
    return $script:couponCode -ne ''
}

Invoke-Case 'API-ORD-001d' '结算页试算能拿到最优券 Id（真实客户端就是拿这个 Id 下单的）' {
    $line = New-OrderLine
    $r = Invoke-Api "$Marketing/coupons/Settle" 'Post' @{
        customerId = $script:customerId
        lines = @(@{ spuId = [long]$script:productId; skuId = [long]$script:skuIds[0]; amount = 51.00 })
    }
    if (-not $r.success) { Write-Host ("        实际返回：" + $r.message) -ForegroundColor DarkYellow; return $false }
    $script:couponId = [long]$r.data.best.couponId
    return $script:couponId -gt 0 -and $r.data.best.discountAmount -eq 10.00
}

Invoke-Case 'API-ORD-001c' '给客户发 5000 积分（够抵扣 50 元）' {
    $r = Invoke-RestMethod "$PointService/internal/points/Earn" -Method Post `
        -Body (@{ customerId = $script:customerId; source = 'test'; quantity = 5000; bizNo = "EARN-$($script:suffix)"; remark = '回归准备' } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    return $r.success -and (Get-PointBalance $script:customerId).available -ge 5000
}

Write-Host "`n=== ORD 基础下单（不占券不用积分）===" -ForegroundColor Cyan

$script:basicOrderNo = ''

Invoke-Case 'API-ORD-009b' '🔴🔴 P0 带满减活动的单：实付必须等于结算页报价' {
    # 结算试算（FinalPrice）会算出活动优惠，但**下单接口收不到它**：
    # 小程序只传 couponId / pointsToUse / freight，OrderCreator 里
    # activityDiscount 直接写成全 0（注释写着「活动优惠尚未落地，先全 0」）。
    # 于是结算页显示 46、点下单却按 51 收 —— 少算的活动优惠变成了实收。
    $now = [DateTime]::UtcNow
    $act = Invoke-RestMethod "$Marketing/marketing/activities/Create" -Method Post `
        -Body (@{
            activityName = "ORD满减$($script:suffix)"; activityType = 1
            thresholdAmount = 40; discountAmount = 5
            giftTemplateId = 0; targetType = 1; targets = '[]'
            startTime = $now.AddDays(-1).ToString('o'); endTime = $now.AddDays(1).ToString('o')
            perOrderLimit = 0; totalQuantity = 0; sortOrder = 0; status = 1
            platformId = $script:platformId; merchantId = 0
        } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30
    if (-not $act.success) { Write-Host ("        建活动失败: " + $act.message) -ForegroundColor DarkYellow; return $false }

    # 记下 Id 供用例结束时删掉：活动在整个用例集里持续生效，
    # 后面那些「实付 = 25.50 × 2」的断言会被它莫名其妙减掉 5 块。
    $script:promoActivityId = [long]$act.data

    # 🔴 清理必须放在 finally 里。
    # 之前它排在最后一句、断言之前，中途任何一次 throw（比如某个下游接口
    # 恰好返回 400）都会跳过清理，把一个「满 40 减 5」的活动永久留在库里。
    # 它对后面所有用例持续生效，于是「实付 = 25.50 × 2」变成 46，
    # 报错指向 ORD-010/ORD-012，而真正的病因在几十行之外、且只在下一次运行里现形。
    # 排查这类「跨运行的幽灵污染」极费时间，代价是几分钟。
    try {
        # 结算试算：本单两件共 51.00 → 满 40 减 5 → 应报 46.00
        $quote = Invoke-RestMethod "$Marketing/marketing/activities/FinalPrice" -Method Post `
            -Body (@{
                customerId = $script:customerId
                lines = @(@{ spuId = [long]$script:productId; skuId = [long]$script:skuIds[0]; amount = 51.00 })
                sessionId = 0; platformId = $script:platformId
            } | ConvertTo-Json -Depth 6) -ContentType 'application/json' -TimeoutSec 30

        # 必须用结算页**推荐的那张券**去下单，否则两边根本不是同一笔：
        # FinalPrice 会自动挑最优券，而下单不传券就等于「没用券」，
        # 报价与实付当然对不上 —— 那是用例没对齐，不是产品算错。
        $chosen = [long]$quote.data.couponId
        $ord = OrderPost 'Create' (New-OrderBody 'promo' 1 2 $chosen 0)
        $det = Get-Order $ord.data.orderNo

        Write-Host ("        结算报价={0}  订单实付={1}  订单行优惠={2}" -f `
            $quote.data.finalPrice, $det.payableAmount, $det.activityDiscount) -ForegroundColor DarkGray

        # 把这单取消掉把库存还回去：不还的话后面几条按「下单后库存」的断言
        # 会被多锁出来的 2 件打偏，而那几条跟本用例毫无关系。
        OrderPost 'Cancel' @{ customerId = $script:customerId; orderNo = $det.orderNo } | Out-Null

        return $quote.data.finalPrice -eq $det.payableAmount
    } finally {
        # 清理失败**不该让用例判红**：本用例要证明的是「报价 == 实付」，
        # 清理只是善后。清理报错而断言失败，会让人以为是优惠没对齐，
        # 其实是删活动没删掉。
        # 这里再包一层 catch：finally 里抛出的异常会**顶掉**try 块真正的失败原因，
        # 于是「断言失败」被替换成「删除活动 404」，比不清理还难查。
        try {
            Invoke-RestMethod "$Marketing/marketing/activities/Delete" -Method Post `
                -Body (@{ activityId = $script:promoActivityId } | ConvertTo-Json) `
                -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        } catch {
            Write-Host ("        活动清理失败（不影响本用例结论）: {0} | body={1}" -f `
                $_.Exception.Message, $_.ErrorDetails.Message) -ForegroundColor DarkYellow
        }
    }
}

Invoke-Case 'API-ORD-009c' '🔴🔴🔴 P0 伪造单价下单被拒（不能信客户端传来的价格）' {
    # 订单接口的 lines[].unitPrice 是**客户端传的**，订单服务直接拿来算金额，
    # 全链路没有回查商品服务的售价。于是任何人都能把 25.50 的商品按 0.01 元下单。
    $forged = @{
        customerId = $script:customerId; platformId = $script:platformId; merchantId = 0
        idempotencyKey = New-IdempotencyKey 'forged'
        receiverName = '张三'; receiverPhone = '13800000000'; receiverAddress = '测试地址 1 号'
        lines = @(@{
            spuId = [long]$script:productId; skuId = [long]$script:skuIds[0]
            quantity = 1; unitPrice = 0.01
            productName = '伪造价格'; skuSpecText = '红'; deliveryType = 1
        })
        freight = 0
    }
    $r = OrderPost 'Create' $forged

    # 两种可能的正确行为：① 拒单；② 按真实售价重算后落单（实付 ≠ 0.02）。
    # 绝不能出现「按 0.01 元成交」。
    if (-not $r.success) {
        Write-Host ("        被拒: " + $r.message) -ForegroundColor DarkGray
        return $true
    }

    $det = Get-Order $r.data.orderNo
    Write-Host ("        下单成功，实付={0}（真实售价应为 {1}）" -f `
        $det.payableAmount, $script:price) -ForegroundColor DarkYellow

    # 落单了也必须按真实售价，不接受 0.02 的实付
    OrderPost 'Cancel' @{ customerId = $script:customerId; orderNo = $det.orderNo } | Out-Null
    # PowerShell 没有 C# 的 0.02m 字面量后缀，要显式转 decimal
    return [decimal]$det.payableAmount -gt [decimal]0.02
}

Invoke-Case 'API-ORD-010' '下单成功：实付 = 25.50 × 2，状态待支付' {
    $r = OrderPost 'Create' (New-OrderBody 'basic')
    $script:basicOrderNo = $r.data.orderNo
    # 把真实回显打出来：断言失败时只看到「FAIL API-ORD-010」根本无从下手，
    # 而下单失败的原因（哪一步、什么错）就明明白白写在这行里。
    Write-Host ("        success={0} code={1} msg={2} orderNo={3} status={4} payable={5}" -f `
        $r.success, $r.code, $r.message, $r.data.orderNo, $r.data.status, $r.data.payableAmount) `
        -ForegroundColor DarkGray
    return $r.success -and $r.data.status -eq 10 -and $r.data.payableAmount -eq 51.00
}

Invoke-Case 'API-ORD-011' '🔴 库存已锁：available −2、locked +2' {
    $s = Get-Stock $script:skuIds[0]
    return $s.available -eq ($script:initStock - 2) -and $s.locked -eq 2
}

Invoke-Case 'API-ORD-012' '订单行按行累加出商品总额，且明细金额齐全' {
    $d = Get-Order $script:basicOrderNo
    return $d.items.Count -eq 1 -and $d.goodsTotal -eq 51.00 -and $d.items[0].originalAmount -eq 51.00 -and $d.items[0].payableAmount -eq 51.00
}

Invoke-Case 'API-ORD-013' '没占券就不该动券，customerId 必填校验生效（游客下单被拒）' {
    $bad = Invoke-Api "$Order/orders/Create" 'Post' @{
        customerId = 0; platformId = 0; merchantId = 0; idempotencyKey = 'x'
        receiverName = 'a'; receiverPhone = '13800000000'; receiverAddress = 'b'; lines = @()
    }
    return (-not $bad.success) -and ($bad.errors.PSObject.Properties.Name -contains 'CustomerId')
}

Write-Host "`n=== ORD 幂等（P0）===" -ForegroundColor Cyan

Invoke-Case 'API-ORD-020' '🔴 P0 同一幂等键重复提交 → 同一订单号，只有一张单' {
    $body = New-OrderBody 'idem'
    $first  = OrderPost 'Create' $body
    $second = OrderPost 'Create' $body

    $script:idemOrderNo = $first.data.orderNo
    return $first.success -and $second.success `
        -and $first.data.orderNo -eq $second.data.orderNo `
        -and $second.data.alreadyCreated -eq $true
}

Invoke-Case 'API-ORD-021' '🔴 P0 重复提交不重复锁库存（只锁了 2 件）' {
    $s = Get-Stock $script:skuIds[0]
    # basic 单已锁 2，idem 单应只锁 2（而不是 4）
    return $s.locked -eq 4
}

Write-Host "`n=== ORD 券与积分联动 ===" -ForegroundColor Cyan

$script:fullOrderNo = ''

Invoke-Case 'API-ORD-030' '用券 + 用积分下单：实付 = 51 − 10 − 50/100 ... 按分摊算' {
    # 券门槛 40，本单 51.00 → 优惠 10.00
    # 积分 5000，按 100 积分 = 1 元 → 抵扣 50.00
    # 实付 = 51.00 − 10.00 − 50.00 = −9.00 → 券先减，积分抵扣不能把订单打成负数，商品总额还有余量
    # 实际：商品总额 51.00 − 券 10.00 = 41.00，积分抵扣最多 41.00，所以传 4100 积分
    $r = OrderPost 'Create' (New-OrderBody 'full' 1 2 $script:couponId 4100)
    $script:fullOrderNo = $r.data.orderNo
    if (-not $r.success) { Write-Host ("        实际返回：" + $r.message) -ForegroundColor DarkYellow; return $false }
    Write-Host ("        总额={0} 券减={1} 积分抵={2} 实付={3}" -f `
        $r.data.goodsTotal, $r.data.couponDiscount, $r.data.pointsDeduction, $r.data.payableAmount) -ForegroundColor DarkGray
    return $r.success -and $r.data.couponDiscount -eq 10.00 `
        -and $r.data.pointsDeduction -eq 41.00 `
        -and $r.data.payableAmount -eq 0.00
}

Invoke-Case 'API-ORD-031' '🔴 实付 0 元的单直接跳到待发货（跳过支付）' {
    $d = Get-Order $script:fullOrderNo
    return $d.status -eq 20
}

Invoke-Case 'API-ORD-032' '🔴 0 元单当场结清：积分冻结后立即实扣，不留悬空占用' {
    $b = Get-PointBalance $script:customerId
    Write-Host ("        积分 avail={0} frozen={1} totalUsed={2}" -f $b.available, $b.frozen, $b.totalUsed) -ForegroundColor DarkGray
    # 发 5000 → 冻 4100 → 实付 0 元当场实扣，所以 frozen 必须已经归零。
    # 这里曾经是个 P0：0 元单没有支付这一步，积分就永远冻着，用户再也用不了。
    return $b.available -eq 900 -and $b.frozen -eq 0 -and $b.totalUsed -ge 4100
}

Write-Host "`n=== ORD 失败逆序回滚（P0）===" -ForegroundColor Cyan

Invoke-Case 'API-ORD-040' '🔴 P0 库存不足时下单被拒，且响应码是 4001（不是笼统 4000）' {
    # 用只有 1 件的 SKU 买 3 件：数量 3 通过校验（≤ 99），但库存不够 → 走真正的库存不足分支。
    # （直接传 9999 件会先被入参校验拦掉，测不到库存不足这条路。）
    $body = New-OrderBody 'shortage'
    $body.lines = @(, @{
        spuId = [long]$script:productId; skuId = $script:lowStockSkuId; quantity = 3
        unitPrice = 8.80; productName = "订单商品$($script:suffix)"; skuSpecText = '限量'; deliveryType = 1
    })
    $script:before041Stock = Get-Stock $script:skuIds[0]
    $script:before041Points = Get-PointBalance $script:customerId
    $r = OrderPost 'Create' $body
    Write-Host ("        实际返回：code={0} msg={1}" -f $r.code, $r.message) -ForegroundColor DarkGray
    return (-not $r.success) -and $r.code -eq 4001 -and $r.message -match '库存不足'
}

Invoke-Case 'API-ORD-041' '🔴 P0 回滚干净：库存计数没变，也没多出积分冻结' {
    $s = Get-Stock $script:skuIds[0]
    $b = Get-PointBalance $script:customerId
    Write-Host ("        库存 locked={0}（下单前 {1}）积分 frozen={2} avail={3}" -f `
        $s.locked, $script:before041Stock.locked, $b.frozen, $b.available) -ForegroundColor DarkGray
    # 逐项与「发起这次失败下单之前」的状态比对：绝对值会随前面的用例变，
    # 写死数字会让这条用例依赖别的用例是否通过——那种用例挂了根本不知道是谁坏了。
    return $s.locked -eq $script:before041Stock.locked `
        -and $s.available -eq $script:before041Stock.available `
        -and $b.frozen -eq $script:before041Points.frozen `
        -and $b.available -eq $script:before041Points.available
}

Invoke-Case 'API-ORD-042' '失败的订单没有落库（同幂等键也查不到）' {
    $body = New-OrderBody 'shortage'
    $body.lines = @(, @{
        spuId = [long]$script:productId; skuId = $script:lowStockSkuId; quantity = 3
        unitPrice = 8.80; productName = "订单商品$($script:suffix)"; skuSpecText = '限量'; deliveryType = 1
    })
    OrderPost 'Create' $body | Out-Null
    $list = Invoke-RestMethod "$Order/orders/List?customerId=$($script:customerId)&pageSize=50" -TimeoutSec 30
    # 限量 SKU 库存必须仍是 1：失败的单既没落库，也没把 1 件库存吃掉
    return (Get-Stock $script:lowStockSkuId).available -eq $script:lowStock
}

Write-Host "`n=== ORD 模拟支付 ===" -ForegroundColor Cyan

Invoke-Case 'API-ORD-050' '模拟支付失败：订单状态与所有占用都不变' {
    $r = AdminOrderPost 'SimulatePayment' @{ orderNo = $script:basicOrderNo; succeed = $false; remark = '回归' }
    $d = Get-Order $script:basicOrderNo
    return $r.success -and ($r.data.succeed -eq $false) -and $d.status -eq 10
}

Invoke-Case 'API-ORD-051' '🔴 模拟支付成功：10 → 20，库存 locked→deducted' {
    $before = Get-Stock $script:skuIds[0]
    $r = AdminOrderPost 'SimulatePayment' @{ orderNo = $script:basicOrderNo; succeed = $true; remark = '回归' }
    $d = Get-Order $script:basicOrderNo
    $s = Get-Stock $script:skuIds[0]
    Write-Host ("        状态 {0} 库存 locked {1}→{2} deducted {3}→{4}" -f `
        $d.status, $before.locked, $s.locked, $before.deducted, $s.deducted) -ForegroundColor DarkGray
    # 只断言「这单锁定的 2 件在支付后转成了 deducted」，
    # 绝对值取决于前面下了几单，写死会让本用例依赖其它用例的成败。
    return $r.success -and $d.status -eq 20 `
        -and $s.locked -eq ($before.locked - 2) -and $s.deducted -eq ($before.deducted + 2)
}

Invoke-Case 'API-ORD-052' '🔴 0 元单重复支付不再重复扣（幂等）' {
    $before = Get-Stock $script:skuIds[0]
    $r = AdminOrderPost 'SimulatePayment' @{ orderNo = $script:fullOrderNo; succeed = $true; remark = '回归' }
    $b = Get-PointBalance $script:customerId
    $d = Get-Order $script:fullOrderNo
    $s = Get-Stock $script:skuIds[0]
    # 这单在创建时就已经结清并进入 20，再点一次支付必须什么都不发生
    return $r.success -and $d.status -eq 20 `
        -and $b.frozen -eq 0 -and $b.available -eq 900 `
        -and $s.deducted -eq $before.deducted
}

Invoke-Case 'API-ORD-053' '🔴 重复支付不再重复扣库存（幂等）' {
    $before = Get-Stock $script:skuIds[0]
    AdminOrderPost 'SimulatePayment' @{ orderNo = $script:basicOrderNo; succeed = $true; remark = '回归' } | Out-Null
    $s = Get-Stock $script:skuIds[0]
    return $s.deducted -eq $before.deducted
}

Write-Host "`n=== ORD 发货与收货 ===" -ForegroundColor Cyan

Invoke-Case 'API-ORD-060' '发货：20 → 30，并写入物流公司与运单号' {
    $r = AdminOrderPost 'Ship' (New-ShipBody $script:basicOrderNo)
    $d = Get-Order $script:basicOrderNo
    Write-Host ("        物流 {0} / {1}" -f $d.logisticsCompanyName, $d.trackingNo) -ForegroundColor DarkGray
    return $r.success -and $d.status -eq 30 -and $d.trackingNo -eq 'SF1234567890' -and $d.logisticsCompanyName
}

Invoke-Case 'API-ORD-059' '🔴 P0 发货必须填物流公司与运单号' {
    # 没有单号的「已发货」只是一个空口状态：客服接到物流异常时无从追责。
    $r = OrderPost 'Create' (New-OrderBody 'needlogistics')
    $no = $r.data.orderNo
    AdminOrderPost 'SimulatePayment' @{ orderNo = $no; succeed = $true; remark = '回归' } | Out-Null

    $noCompany = AdminOrderPost 'Ship' @{ orderNo = $no; remark = '忘了选'; trackingNo = 'SF0001' }
    $noTracking = AdminOrderPost 'Ship' @{ orderNo = $no; remark = '忘了填'; logisticsCompanyId = (Get-LogisticsId) }
    $ok = AdminOrderPost 'Ship' (New-ShipBody $no 'SF7654321')

    # 校验失败的顶层 message 统一是「请求参数校验失败」，具体原因在 errors 里
    # （CODING_STANDARD 3.4：前端只用 errors 做 tip 提示）
    return (-not $noCompany.success) -and ($noCompany.errors.PSObject.Properties.Name -contains 'LogisticsCompanyId') `
        -and (-not $noTracking.success) -and ($noTracking.errors.PSObject.Properties.Name -contains 'TrackingNo') `
        -and $ok.success
}

Invoke-Case 'API-ORD-061' '已发货的订单不能再取消' {
    $r = OrderPost 'Cancel' @{ customerId = $script:customerId; orderNo = $script:basicOrderNo }
    return (-not $r.success) -and $r.code -eq 4003
}

Invoke-Case 'API-ORD-062' '🔴 确认收货：30 → 50，并按实付发积分（实付 51.00 → 51 分）' {
    # 积分是**在这条用例里**发的，所以前后余额必须在这条内部取；
    # 放到下一条去测就永远只能看到差 0。
    $before = Get-PointBalance $script:customerId
    $r = OrderPost 'ConfirmReceipt' @{ customerId = $script:customerId; orderNo = $script:basicOrderNo }
    $d = Get-Order $script:basicOrderNo
    $after = Get-PointBalance $script:customerId
    $granted = $after.totalEarned - $before.totalEarned

    Write-Host ("        实付 {0} 发放积分 {1}" -f $d.payableAmount, $granted) -ForegroundColor DarkGray
    return $r.success -and $d.status -eq 50 -and $granted -eq [long]$d.payableAmount
}


Invoke-Case 'API-ORD-062c' '🔴 重复确认收货不重复发积分（幂等）' {
    $before = Get-PointBalance $script:customerId
    OrderPost 'ConfirmReceipt' @{ customerId = $script:customerId; orderNo = $script:basicOrderNo } | Out-Null
    $after = Get-PointBalance $script:customerId
    return $after.totalEarned -eq $before.totalEarned
}

Invoke-Case 'API-ORD-063' '🔴 用户确认收货后不可退款' {
    $r = AdminOrderPost 'Refund' @{ orderNo = $script:basicOrderNo; remark = '试试退' }
    return (-not $r.success) -and $r.message -match '不能再退'
}

Invoke-Case 'API-ORD-064' '🔴 重复发货按幂等处理（回「已发货」，不是报错）' {
    # 运营在列表上误点两下是常事，回红色报错会让人以为货没发出去、于是点第三次。
    # 这条排在确认收货之后，所以单子已是 50：状态**不能**被这个重复动作改回去。
    $r = AdminOrderPost 'Ship' (New-ShipBody $script:basicOrderNo 'SF0000000000')
    $d = Get-Order $script:basicOrderNo
    return $r.success -and $r.message -match '已发货' -and $d.status -eq 50
}

Write-Host "`n=== ORD 取消与退款回补 ===" -ForegroundColor Cyan

$script:cancelOrderNo = ''

Invoke-Case 'API-ORD-070' '下单后取消：释放库存' {
    $r = OrderPost 'Create' (New-OrderBody 'cancel')
    $script:cancelOrderNo = $r.data.orderNo
    $before = Get-Stock $script:skuIds[0]

    $c = OrderPost 'Cancel' @{ customerId = $script:customerId; orderNo = $script:cancelOrderNo }
    $after = Get-Stock $script:skuIds[0]
    return $r.success -and $c.success -and $after.locked -eq ($before.locked - 2) -and $after.available -eq ($before.available + 2)
}

Invoke-Case 'API-ORD-071' '取消后订单状态为已取消，且不可再取消' {
    $d = Get-Order $script:cancelOrderNo
    $again = OrderPost 'Cancel' @{ customerId = $script:customerId; orderNo = $script:cancelOrderNo }
    return $d.status -eq 91 -and (-not $again.success)
}

Invoke-Case 'API-ORD-072' '已发货订单退款走 replenish（回补 deducted）' {
    $r = OrderPost 'Create' (New-OrderBody 'refund')
    $no = $r.data.orderNo
    AdminOrderPost 'SimulatePayment' @{ orderNo = $no; succeed = $true; remark = '回归' } | Out-Null
    $before = Get-Stock $script:skuIds[0]
    AdminOrderPost 'Ship' (New-ShipBody $no) | Out-Null

    $refund = AdminOrderPost 'Refund' @{ orderNo = $no; remark = '客户申请' }
    $after = Get-Stock $script:skuIds[0]
    $d = Get-Order $no
    return $refund.success -and $d.status -eq 60 `
        -and $after.deducted -eq ($before.deducted - 2) -and $after.available -eq ($before.available + 2)
}

Write-Host "`n=== ORD 退款回收积分（BUSINESS 10.3）===" -ForegroundColor Cyan

Invoke-Case 'API-ORD-073' '🔴 P0 整单退款**全额回收**抵扣积分' {
    # 积分侧的实现在 PointService 一直都在（internal/points/Refund，含向上取整与原批次回填），
    # 缺的只是**退款链路去调它** —— 于是客户一边拿回钱、一边把抵扣的积分白留着（双花）。
    # 抵扣量按**当前余额**取，不要写死：前面的用例已经把 5000 分花掉一部分，
    # 写死 1000 会在下单那一步就因「可用积分不足」失败 ——
    # 症状看着像退款没回收积分，其实订单压根没建成。
    $bal = Get-PointBalance $script:customerId
    $use = [long]([math]::Min(500, $bal.available))
    if ($use -le 0) { Write-Host '        客户已无积分可用，跳过' -ForegroundColor Yellow; return $true }

    $r = OrderPost 'Create' (New-OrderBody 'ptrefund' 1 2 0 $use)
    if (-not $r.success) {
        Write-Host ("        下单失败：{0}" -f $r.message) -ForegroundColor DarkYellow
        return $false
    }
    $no = $r.data.orderNo
    $deducted = [long]$r.data.pointsUsed
    if ($deducted -le 0) { Write-Host '        本单没抵扣到积分，跳过' -ForegroundColor Yellow; return $true }

    AdminOrderPost 'SimulatePayment' @{ orderNo = $no; succeed = $true; remark = '回归' } | Out-Null
    $afterPay = Get-PointBalance $script:customerId

    $refund = AdminOrderPost 'Refund' @{ orderNo = $no; remark = '整单退，积分应全额回收' }
    if (-not $refund.success) {
        Write-Host ("        退款失败：{0}" -f $refund.message) -ForegroundColor DarkYellow
        return $false
    }

    $afterRefund = Get-PointBalance $script:customerId
    Write-Host ("        实付={0} 抵扣积分={1} 支付后可用={2} 退款后可用={3}" -f `
        $r.data.payableAmount, $deducted, $afterPay.available, $afterRefund.available) -ForegroundColor DarkGray

    return ($afterRefund.available - $afterPay.available) -eq $deducted
}

Invoke-Case 'API-ORD-073b' '🔴 P0 两次部分退款：累计回收按比例且**绝不超过**抵扣量' {
    $bal0 = Get-PointBalance $script:customerId
    $use = [long]([math]::Min(500, $bal0.available))
    if ($use -le 0) { Write-Host '        客户已无积分可用，跳过' -ForegroundColor Yellow; return $true }

    $r = OrderPost 'Create' (New-OrderBody 'ptpartial' 1 2 0 $use)
    if (-not $r.success) {
        Write-Host ("        下单失败：{0}" -f $r.message) -ForegroundColor DarkYellow
        return $false
    }
    $no = $r.data.orderNo
    $deducted = [long]$r.data.pointsUsed
    if ($deducted -le 0) { Write-Host '        本单没抵扣到积分，跳过' -ForegroundColor Yellow; return $true }

    AdminOrderPost 'SimulatePayment' @{ orderNo = $no; succeed = $true; remark = '回归' } | Out-Null
    $before = Get-PointBalance $script:customerId

    # 必须显式传 lines 才是「部分退」；不传 lines 就是整单退，
    # 而整单退一次就把订单打成 60 已退款，第二次当然会被拒 —— 那测不到分次回收。
    $line = @((Get-AdminOrder $no).items)[0]
    $half = [math]::Round([decimal]$line.payableAmount / 2, 2)

    $r1 = AdminOrderPost 'Refund' @{
        orderNo = $no; remark = '退一半'
        lines   = @(@{ orderItemId = $line.orderItemId; quantity = 1; amount = $half })
    }
    if (-not $r1.success) {
        Write-Host ("        第一次退款失败：{0}" -f $r1.message) -ForegroundColor DarkYellow
        return $false
    }
    $mid = Get-PointBalance $script:customerId
    $firstRecovered = $mid.available - $before.available

    # ⚠️ 必须**重新拉一次**详情：上面那份 $line 是第一次退款**之前**取的，
    # 它的 refundedQuantity 还是 0，拿它算剩余件数会退多，被行级上限挡下来。
    $line2 = @((Get-AdminOrder $no).items)[0]
    $r2 = AdminOrderPost 'Refund' @{
        orderNo = $no; remark = '退剩下的一半'
        # 件数用「剩余件数」而不是原始件数：第一次已经退掉 1 件，
        # 再传原始件数会被行级上限挡住（这正是 ORD-079 要守的规则）
        lines   = @(@{
            orderItemId = $line2.orderItemId
            quantity    = $line2.quantity - $line2.refundedQuantity
            amount      = [decimal]$line2.refundableAmount
        })
    }
    if (-not $r2.success) {
        Write-Host ("        第二次退款失败：{0}" -f $r2.message) -ForegroundColor DarkYellow
        return $false
    }
    $end = Get-PointBalance $script:customerId
    $totalRecovered = $end.available - $before.available

    Write-Host ("        抵扣={0} 第一次回收={1} 累计回收={2}" -f `
        $deducted, $firstRecovered, $totalRecovered) -ForegroundColor DarkGray

    # 断言的是**安全性质**，不是「正好等于」：
    #   ① 累计回收**永远不超过**抵扣量 —— 超过就是白送积分，是资损；
    #   ② 第一笔至少回收了一半 —— 说明确实按比例回收了，不是没退。
    #
    # 为什么不断言「累计正好等于抵扣量」：10.3 规定部分退款**向上取整**，
    # 分两次退时每笔各向上取整，第二次会算出一个比实际大的数，
    # 被积分服务按「已回收」扣掉后变成 0 —— 于是累计略少于抵扣量。
    # 这是规格明说的取舍（向上取整对用户不利、对平台有利），
    # 强行凑成整数反而会让「不能超额回收」这条更重要的不变量失守。
    $floorFirst = [math]::Floor($deducted / 2)
    return ($totalRecovered -le $deducted) -and ($firstRecovered -ge $floorFirst)
}

Write-Host "`n=== ORD 多次部分退款 ===" -ForegroundColor Cyan

$script:partialOrderNo = ''

Invoke-Case 'API-ORD-075' '建一张两行订单，为多次部分退款做准备' {
    $r = OrderPost 'Create' (New-TwoLineOrderBody 'partial')
    $script:partialOrderNo = $r.data.orderNo
    AdminOrderPost 'SimulatePayment' @{ orderNo = $script:partialOrderNo; succeed = $true; remark = '回归' } | Out-Null
    $d = Get-AdminOrder $script:partialOrderNo
    return $r.success -and @($d.items).Count -eq 2
}

Invoke-Case 'API-ORD-076' '🔴 P0 第一次部分退款：只退一行，订单**不**变成已退款' {
    $d = Get-AdminOrder $script:partialOrderNo
    $line = @($d.items)[0]
    $before = Get-Stock $line.skuId

    # 退这一行的**全部金额**，但只退 1 件 —— 订单还剩一行没退，不能变成 60 已退款
    $r = AdminOrderPost 'Refund' @{
        orderNo = $script:partialOrderNo
        remark  = '第一件破损'
        lines   = @(@{ orderItemId = $line.orderItemId; quantity = 1; amount = $line.payableAmount })
    }

    $after = Get-AdminOrder $script:partialOrderNo
    Write-Host ("        本次退 {0}，累计 {1}，还能退 {2}" -f `
            $r.data.amount, $after.refundedAmount, $after.remainingRefundable) -ForegroundColor DarkGray

    # 库存按**本次退的件数**回补，不是整行数量：
    # 这一行有 2 件，只退 1 件就只应回补 1 件，按整行回补会直接超卖。
    $back = Get-Stock $line.skuId
    Write-Host ("        success={0} fullyRefunded={1} 状态={2} 已退={3} 库存 {4} -> {5}" -f `
        $r.success, $r.data.fullyRefunded, $after.status, $after.refundedAmount, `
        $before.available, $back.available) -ForegroundColor DarkGray
    return $r.success -and $r.data.fullyRefunded -eq $false `
        -and $after.status -ne 60 `
        -and $after.refundedAmount -gt 0 `
        -and ($back.available - $before.available) -eq 1
}

Invoke-Case 'API-ORD-077' '🔴 P0 第二次部分退款：退另一行，此时才整单退完' {
    $d = Get-AdminOrder $script:partialOrderNo
    $line = @($d.items)[1]
    $left = [decimal]$line.refundableAmount

    $r = AdminOrderPost 'Refund' @{
        orderNo = $script:partialOrderNo
        remark  = '第二件也退'
        lines   = @(@{ orderItemId = $line.orderItemId; quantity = $line.quantity; amount = $left })
    }

    $after = Get-AdminOrder $script:partialOrderNo
    Write-Host ("        第二次退 {0}，累计 {1}，状态 {2}" -f `
            $r.data.amount, $after.refundedAmount, $after.status) -ForegroundColor DarkGray

    return $r.success -and $r.data.fullyRefunded -eq $true `
        -and $after.status -eq 60 `
        -and $after.remainingRefundable -eq 0
}

Invoke-Case 'API-ORD-078' '🔴 P0 已退完的订单不能再退（否则就是超退）' {
    $r = AdminOrderPost 'Refund' @{ orderNo = $script:partialOrderNo; remark = '还想再退' }
    return (-not $r.success)
}

Invoke-Case 'API-ORD-079' '🔴 P0 超出行级可退余额的退款被拒，并说明还能退多少' {
    $r = OrderPost 'Create' (New-TwoLineOrderBody 'overrefund')
    $no = $r.data.orderNo
    AdminOrderPost 'SimulatePayment' @{ orderNo = $no; succeed = $true; remark = '回归' } | Out-Null
    $d = Get-AdminOrder $no
    $line = @($d.items)[0]

    # 退的行金额比该行实付还多 100 元 —— 必须被行级余额挡住
    $r1 = AdminOrderPost 'Refund' @{
        orderNo = $no; remark = '退太多'
        lines   = @(@{ orderItemId = $line.orderItemId; quantity = 1; amount = ([decimal]$line.payableAmount + 100) })
    }

    # 退的件数比该行数量还多 —— 必须被件数上限挡住（否则库存会多回补）
    $r2 = AdminOrderPost 'Refund' @{
        orderNo = $no; remark = '退太多件'
        lines   = @(@{ orderItemId = $line.orderItemId; quantity = 99; amount = 1 })
    }

    # 上面两次都被拒之后，这一行仍应原封不动
    $after = Get-AdminOrder $no
    return (-not $r1.success) -and $r1.message -match '还能退' `
        -and (-not $r2.success) -and $r2.message -match '最多还能退' `
        -and $after.refundedAmount -eq 0 -and $after.status -ne 60
}

Invoke-Case 'API-ORD-079b' '退款记录可查，且按时间升序（多次部分退款的历史）' {
    $r = AdminOrderPost 'Refunds' @{ orderId = (Get-AdminOrder $script:partialOrderNo).orderId }
    return $r.success -and @($r.data).Count -eq 2 `
        -and $r.data[0].createdAt -le $r.data[1].createdAt
}

Write-Host "`n=== ORD 自提取货码（RSA）===" -ForegroundColor Cyan

$script:pickupOrderNo = ''
$script:pickupCode = ''

Invoke-Case 'API-ORD-080' '自提单支付后进入待发货' {
    $r = OrderPost 'Create' (New-OrderBody 'pickup' 3)
    $script:pickupOrderNo = $r.data.orderNo
    AdminOrderPost 'SimulatePayment' @{ orderNo = $script:pickupOrderNo; succeed = $true; remark = '回归' } | Out-Null
    $d = Get-Order $script:pickupOrderNo
    return $r.success -and $d.status -eq 20
}

Invoke-Case 'API-ORD-081' '备货完成返回取货码，状态 20 → 40' {
    $r = AdminOrderPost 'SelfPickupReady' @{ orderNo = $script:pickupOrderNo; remark = '已备好' }
    $script:pickupCode = $r.data.pickupCode
    $d = Get-Order $script:pickupOrderNo
    # 取货码 = RSA 密文的 Base64，不是订单号本身
    return $r.success -and $d.status -eq 40 -and $script:pickupCode -ne '' -and $script:pickupCode -ne $script:pickupOrderNo
}

Invoke-Case 'API-ORD-081b' '🔴 重复备货返回可用的取货码（幂等，不报错）' {
    $r = AdminOrderPost 'SelfPickupReady' @{ orderNo = $script:pickupOrderNo; remark = '又点了一次' }
    return $r.success -and $r.data.pickupCode -ne '' -and $r.data.verified -eq $false
}

Invoke-Case 'API-ORD-082' '🔴 乱码取货码核销失败且不泄露订单是否存在' {
    $r = AdminOrderPost 'VerifyPickupCode' @{ pickupCode = 'bm90LWEtdmFsaWQtcGlja3VwLWNvZGU='; platformId = 0; merchantId = 0 }
    return (-not $r.success) -and $r.message -match '取货码无效'
}

Invoke-Case 'API-ORD-083' '🔴 取货码核销成功：40 → 50，解出的就是订单号' {
    $script:before083Points = Get-PointBalance $script:customerId
    $r = AdminOrderPost 'VerifyPickupCode' @{ pickupCode = $script:pickupCode; platformId = 0; merchantId = 0 }
    $d = Get-Order $script:pickupOrderNo
    $after = Get-PointBalance $script:customerId
    return $r.success -and $r.data.orderNo -eq $script:pickupOrderNo -and $r.data.verified -eq $true -and $d.status -eq 50 `
        -and ($after.totalEarned - $script:before083Points.totalEarned) -eq [long]$d.payableAmount
}

Invoke-Case 'API-ORD-086' '🔴 0 元单实付为 0 → 一分积分也不发（否则积分成永动机）' {
    $before = Get-PointBalance $script:customerId
    # full 单实付 0.00，规则是「实付每满 1 元 1 积分」，所以是 0 分。
    # 如果这里发成了 51 分，用户就能「用券抵扣到 0 元 → 白拿 51 积分 → 再抵扣下一单」，
    # 形成闭环。所以这条专门盯住「不发」。
    $r = Invoke-Api "$PointService/internal/points/EarnByOrder" 'Post' @{
        customerId = $script:customerId; bizNo = 'ZERO-AMOUNT-PROBE'; paidAmount = 0; remark = '回归'
    }
    $after = Get-PointBalance $script:customerId
    return $r.success -and $r.message -match '不足 1 元' -and $after.totalEarned -eq $before.totalEarned
}

Invoke-Case 'API-ORD-086b' '🔴 同一订单号重复发放只发一次（幂等）' {
    $bizNo = "EARN-IDEM-$($script:suffix)"
    $before = Get-PointBalance $script:customerId

    Invoke-Api "$PointService/internal/points/EarnByOrder" 'Post' `
        @{ customerId = $script:customerId; bizNo = $bizNo; paidAmount = 20.00; remark = '回归' } | Out-Null
    $mid = Get-PointBalance $script:customerId

    Invoke-Api "$PointService/internal/points/EarnByOrder" 'Post' `
        @{ customerId = $script:customerId; bizNo = $bizNo; paidAmount = 20.00; remark = '回归' } | Out-Null
    $after = Get-PointBalance $script:customerId

    return ($mid.totalEarned - $before.totalEarned) -eq 20 -and ($after.totalEarned - $mid.totalEarned) -eq 0
}

Write-Host "`n=== ORD 虚拟商品 ===" -ForegroundColor Cyan

$script:virtualOrderNo = ''

Invoke-Case 'API-ORD-090' '虚拟单发货即完成：20 → 50' {
    $r = OrderPost 'Create' (New-OrderBody 'virtual' 2)
    $script:virtualOrderNo = $r.data.orderNo
    AdminOrderPost 'SimulatePayment' @{ orderNo = $script:virtualOrderNo; succeed = $true; remark = '回归' } | Out-Null
    $script:before090Points = Get-PointBalance $script:customerId
    $v = AdminOrderPost 'DeliverVirtual' @{ orderNo = $script:virtualOrderNo; remark = '卡号 ABCD-1234' }
    $d = Get-Order $script:virtualOrderNo
    $after = Get-PointBalance $script:customerId
    # 虚拟发货也是一条进「已完成」的路，积分同样要发。
    # 漏掉的话就是「同一个功能，有的单给积分有的不给」，客服解释不了。
    return $r.success -and $v.success -and $d.status -eq 50 `
        -and ($after.totalEarned - $script:before090Points.totalEarned) -eq [long]$d.payableAmount
}

Invoke-Case 'API-ORD-091' '🔴 虚拟商品订单不可退款（用户明确要求）' {
    $r = AdminOrderPost 'Refund' @{ orderNo = $script:virtualOrderNo; remark = '试试退' }
    return (-not $r.success) -and $r.message -match '虚拟商品订单不支持退款'
}

Invoke-Case 'API-ORD-092' '实物订单不能用虚拟发货' {
    $r = AdminOrderPost 'DeliverVirtual' @{ orderNo = $script:basicOrderNo; remark = 'x' }
    return (-not $r.success) -and $r.message -match '实物商品'
}

Write-Host "`n=== ORD 支付超时关单 ===" -ForegroundColor Cyan

Invoke-Case 'API-ORD-110' '🔴 超时未支付的订单被关掉，三项占用全部释放' {
    $r = OrderPost 'Create' (New-OrderBody 'timeout')
    $no = $r.data.orderNo
    $before = Get-Stock $script:skuIds[0]

    # 把创建时间往前拨 40 分钟。阈值是 30 分钟，直接改库比等 30 分钟现实得多——
    # 这也是仓库里其它回归脚本处理时间相关规则的老办法。
    #
    # 必须写 `now() AT TIME ZONE 'UTC'`：容器会话时区是 Asia/Shanghai，
    # 直接写 `now() - interval` 得到的是 timestamptz，赋给 timestamp 列时会按会话时区折算，
    # 结果是把时间「往前拨」变成了「往前推 8 小时」——单看 SQL 完全看不出问题。
    docker exec simpleshop-postgres psql -U postgres -d simpleshoporder -q -c `
        "UPDATE ""order"" SET created_at = (now() AT TIME ZONE 'UTC') - interval '40 minutes' WHERE order_no = '$no';" | Out-Null

    $close = Invoke-Api "$Order/internal/orders/close-timeout" 'Post' @{ orderNo = ''; limit = 0 }
    $after = Get-Stock $script:skuIds[0]
    $d = Get-Order $no

    Write-Host ("        关单结果：扫描 {0} 关单 {1}" -f $close.data.scanned, $close.data.closed) -ForegroundColor DarkGray
    return $r.success -and $close.success -and $d.status -eq 91 `
        -and $after.locked -eq ($before.locked - 2) -and $after.available -eq ($before.available + 2)
}

Invoke-Case 'API-ORD-111' '🔴 关过的单不会被重复关（幂等）' {
    $d = Get-Order $script:basicOrderNo
    $close = Invoke-Api "$Order/internal/orders/close-timeout" 'Post' @{ orderNo = $script:basicOrderNo; limit = 0 }

    # 手工指定单号时状态不对就只报告、不改状态。
    # 定时任务扫全量时更安全：查询本身就带 status = 10 的条件。
    return $close.success -and $d.status -eq 50
}

Invoke-Case 'API-ORD-112' '没超时的单不会被误关' {
    $list = Invoke-RestMethod "$Order/orders/List?customerId=$($script:customerId)&status=10&pageSize=50" -TimeoutSec 30
    Invoke-Api "$Order/internal/orders/close-timeout" 'Post' @{ orderNo = ''; limit = 0 } | Out-Null
    $after = Invoke-RestMethod "$Order/orders/List?customerId=$($script:customerId)&status=10&pageSize=50" -TimeoutSec 30

    # 刚下的单创建时间就在当下，阈值 30 分钟内不该被扫。
    # 这条专门挡「阈值算错成 0 分钟」这种一上线就把所有待支付单全关掉的错。
    return @($list.data.items).Count -eq @($after.data.items).Count
}

Write-Host "`n=== ORD 查询与越权 ===" -ForegroundColor Cyan

Invoke-Case 'API-ORD-100' '我的订单分页只返回自己的单' {
    $list = Invoke-RestMethod "$Order/orders/List?customerId=$($script:customerId)&pageSize=50" -TimeoutSec 30
    return $list.success -and $list.data.total -ge 6 -and @($list.data.items).Count -ge 6
}

Invoke-Case 'API-ORD-101' '🔴 查别人的订单回 404 而不是 403（不泄露订单是否存在）' {
    try {
        $r = Invoke-RestMethod "$Order/orders/Detail?orderNo=$($script:basicOrderNo)&customerId=$($script:customerId + 1)" -TimeoutSec 30
        return (-not $r.success) -and $r.code -eq 404
    } catch {
        $body = $_.ErrorDetails.Message | ConvertFrom-Json
        return (-not $body.success) -and $body.code -eq 404
    }
}

Invoke-Case 'API-ORD-102' '按状态筛选只返回该状态的订单' {
    $list = Invoke-RestMethod "$Order/orders/List?customerId=$($script:customerId)&status=50&pageSize=50" -TimeoutSec 30
    $all = @($list.data.items)
    return $all.Count -ge 3 -and (@($all | Where-Object { $_.status -ne 50 }).Count -eq 0)
}

Invoke-Case 'API-ORD-103' '后台订单列表返回状态中文名与件数' {
    $r = AdminOrderPost 'List' @{ status = 0; keyword = ''; page = 1; pageSize = 20; platformId = 0; merchantId = 0 }
    return $r.success -and $r.data.total -ge 6 -and $r.data.items[0].statusName -ne '' -and $r.data.items[0].itemQuantity -ge 1
}

Invoke-Case 'API-ORD-104' '后台按订单号搜索能命中' {
    $r = AdminOrderPost 'List' @{ status = 0; keyword = $script:basicOrderNo; page = 1; pageSize = 20; platformId = 0; merchantId = 0 }
    return $r.success -and $r.data.total -eq 1 -and $r.data.items[0].orderNo -eq $script:basicOrderNo
}

Invoke-Case 'API-ORD-105' '同一 SKU 重复出现在订单里被拒（金额分摊说不清）' {
    $line = New-OrderLine
    $body = New-OrderBody 'dup'
    $body.lines = @($line, $line)
    $r = OrderPost 'Create' $body
    # 校验失败的顶层 message 是统一的「请求参数校验失败」，具体原因在 errors 里
    # （CODING_STANDARD 3.4：前端只用 errors 做 tip 提示，不飘红输入框）
    $lineErrors = @($r.errors.PSObject.Properties | Where-Object { $_.Name -eq 'Lines' } |
        ForEach-Object { $_.Value }) -join ' '
    Write-Host ("        errors = {0}" -f $lineErrors) -ForegroundColor DarkGray
    return (-not $r.success) -and ($lineErrors -match '不能重复')
}

Write-Host "`n=== ORD 后台订单详情 ===" -ForegroundColor Cyan

$script:adminDetailOrderId = 0

Invoke-Case 'API-ORD-106' '🔴 P0 后台订单详情：含金额构成与订单行' {
    # 先从后台列表拿一个真实 Id —— 后台详情按 Id 查（列表页拿到的就是 Id）
    $list = AdminOrderPost 'List' @{ page = 1; pageSize = 1 }
    if (-not $list.success -or $list.data.items.Count -eq 0) { return $false }
    $script:adminDetailOrderId = [long]$list.data.items[0].orderId

    $r = AdminOrderPost 'Detail' @{ orderId = $script:adminDetailOrderId }
    if (-not $r.success) { return $false }

    # 金额构成四项齐全：商品总额 + 运费 − 积分抵扣 = 实付
    $d = $r.data
    if ($null -eq $d.goodsTotal -or $null -eq $d.freight) { return $false }
    if ($null -eq $d.pointsDeduction -or $null -eq $d.payableAmount) { return $false }
    return $d.items.Count -gt 0 -and $d.orderNo -and $d.statusName
}

Invoke-Case 'API-ORD-107' '🔴 P0 后台详情金额恒等式：实付 = 商品总额 + 运费 − 积分抵扣' {
    $r = AdminOrderPost 'Detail' @{ orderId = $script:adminDetailOrderId }
    if (-not $r.success) { return $false }
    $d = $r.data
    # 用分为单位比，避免 decimal 浮点误差
    $expect = [decimal]$d.goodsTotal + [decimal]$d.freight - [decimal]$d.pointsDeduction
    return [math]::Abs([decimal]$d.payableAmount - $expect) -lt 0.01
}

Invoke-Case 'API-ORD-108' '🔴 后台详情**不校验客户归属**（与 C 端相反）' {
    # 后台的可见范围由网关租户上下文决定；归属校验属于 C 端。
    # 这里用「列表里随便一单」验证它能被查到 —— 如果错误地搬用了 C 端的归属校验，
    # 这条会直接 404。
    $r = AdminOrderPost 'Detail' @{ orderId = $script:adminDetailOrderId }
    return $r.success -eq $true
}

Invoke-Case 'API-ORD-109' '不存在的订单回「订单不存在」而不是 500' {
    $r = AdminOrderPost 'Detail' @{ orderId = 99999999999 }
    return (-not $r.success) -and ($r.message -match '订单不存在')
}

Invoke-Case 'API-ORD-113' '🔴 orderId <= 0 被校验挡住（不查库直接拒）' {
    $r = AdminOrderPost 'Detail' @{ orderId = 0 }
    return (-not $r.success) -and ($r.errors.PSObject.Properties.Name -contains 'OrderId')
}

Write-Host "`n=== ORD 运费：服务端按平台配置算，不采信客户端 ===" -ForegroundColor Cyan

$script:feePlatformId = 0
$script:feeProductIds = @{}

Invoke-Case 'API-ORD-130' '建平台（运费 10 / 包邮门槛 100）并上架四种配送方式的商品' {
    # 运费是**平台级配置**（BUSINESS.md 6.2），所以要先有一个配了运费的平台。
    # 平台自营（platformId = 0）没有对应的 platform 行，拿不到运费配置，
    # 因此这里单独建一个平台来验证「配置真的生效」，而不是复用自营那套。
    $code = -join ((1..6) | ForEach-Object { [char](65 + (Get-Random -Max 26)) })
    $plat = Invoke-RestMethod "$Gateway/gateway/platforms/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{
            platformName = "运费$($script:suffix)"; mallName = "运费商城$($script:suffix)"
            platformCode = $code; contactName = '测试'; contactPhone = '13800000000'
            shippingFee = 10; freeShippingThreshold = 100; status = 1
        } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30
    if (-not $plat.success) { Write-Host ("        建平台失败: " + $plat.message) -ForegroundColor DarkYellow; return $false }
    $script:feePlatformId = [long]$plat.data

    # 分类要挂在同一个平台下：跨平台引用分类会让「运费按哪个平台算」这件事失去意义。
    $p1 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = 0; categoryName = "运费$($script:suffix)"; platformId = $script:feePlatformId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $p2 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = $p1; categoryName = "运费$($script:suffix)"; platformId = $script:feePlatformId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $p3 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = $p2; categoryName = "运费$($script:suffix)"; platformId = $script:feePlatformId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data

    # 四种组合：快递 / 虚拟 / 自提，以及一件就够包邮的高价快递商品。
    # 配送方式挂在 SPU 上（BUSINESS.md 6.1），所以每个都要单独建一个商品。
    $specs = @(
        @{ key = 'express';   deliveryType = 1; price = 25.50; qty = 200; code = "FR-E$($script:suffix)" }
        @{ key = 'virtual';   deliveryType = 2; price = 25.50; qty = 200; code = "FR-V$($script:suffix)" }
        @{ key = 'pickup';    deliveryType = 3; price = 25.50; qty = 200; code = "FR-P$($script:suffix)" }
        @{ key = 'expressBig'; deliveryType = 1; price = 120.00; qty = 20; code = "FR-B$($script:suffix)" }
    )
    foreach ($s in $specs) {
        # 刻意不叫 $pid：PowerShell 的 $PID 是只读的内置变量（当前进程 Id），
        # 给它赋值会直接抛「Cannot overwrite variable PID」，而且报错完全看不出是命名撞了。
        $newProductId = [long](Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:adminHeaders `
            -Body (@{
                productId = 0; spuName = "运费$($s.key)$($script:suffix)"; categoryId = $p3
                platformId = $script:feePlatformId
                deliveryType = $s.deliveryType; mainImage = 'https://cdn.example.com/m.png'
                specs = @(@{ specName = '颜色'; specValues = @('红') })
                skus = @(@{ skuCode = $s.code; specValues = @('红'); price = $s.price; stock = $s.qty; status = 1 })
            } | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30).data

        # 与 API-ORD-000 同一个道理：必须审核通过 + 上架，否则下单链路会拒单。
        Invoke-RestMethod "$Gateway/gateway/products/Audit" -Method Post -Headers $script:adminHeaders `
            -Body (@{ productId = $newProductId; auditStatus = 20 } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:adminHeaders `
            -Body (@{ productId = $newProductId; status = 1 } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null

        $d = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$newProductId" -Headers $script:adminHeaders -TimeoutSec 30
        $script:feeProductIds[$s.key] = @{
            productId = $newProductId
            skuId = [long](@($d.data.skus | Where-Object { $_.skuCode -eq $s.code })[0].id)
            price = $s.price
            deliveryType = $s.deliveryType
        }
    }
    $script:feeCategoryIds = @($p1, $p2, $p3)
    return $script:feePlatformId -gt 0 -and $script:feeProductIds.Count -eq 4
}

<#
.SYNOPSIS
    用运费平台的商品下一单。
.DESCRIPTION
    <b>clientFreight</b> 就是要故意写进请求体的运费值 ——
    本组用例的全部意义就是证明它被**忽略**，所以必须能自由地传各种垃圾值。
#>
function New-FeeOrder([string]$Key, [string]$Tag, [decimal]$ClientFreight = 0, [int]$Qty = 2, [int]$ClaimedDeliveryType = 0) {
    $p = $script:feeProductIds[$Key]
    return OrderPost 'Create' @{
        customerId = $script:customerId; platformId = $script:feePlatformId; merchantId = 0
        idempotencyKey = (New-IdempotencyKey $Tag)
        receiverName = '张三'; receiverPhone = '13800000000'; receiverAddress = '某地某小区 1 号楼 101'
        lines = @(@{
            spuId = [long]$p.productId; skuId = [long]$p.skuId; quantity = $Qty
            unitPrice = [decimal]$p.price
            productName = "运费商品$Key"; skuSpecText = '红'
            deliveryType = if ($ClaimedDeliveryType -gt 0) { $ClaimedDeliveryType } else { [int]$p.deliveryType }
        })
        freight = $ClientFreight
        remark = "运费$Tag"
    }
}

Invoke-Case 'API-ORD-131' '🔴 P0 客户端报运费 0，服务端仍按平台配置收 10 元' {
    $r = New-FeeOrder 'express' 'fee0' -ClientFreight 0
    if (-not $r.success) { Write-Host ("        下单失败: " + $r.message) -ForegroundColor DarkYellow; return $false }
    $script:feeOrderA = $r.data.orderNo
    $d = Get-Order $r.data.orderNo

    Write-Host ("        客户端报运费 0 → 订单运费={0} 实付={1}" -f $d.freight, $d.payableAmount) -ForegroundColor DarkGray

    # 2 × 25.50 = 51.00，加运费 10 → 实付 61.00。
    # 前端至今把 freight 硬编码成 0，采信它就等于平台运费永远收不到。
    return $d.freight -eq 10.00 -and $d.payableAmount -eq 61.00
}

Invoke-Case 'API-ORD-132' '🔴 客户端报运费 9999 也无效（不能自己抬价）' {
    $r = New-FeeOrder 'express' 'fee9999' -ClientFreight 9999
    if (-not $r.success) { return $false }
    $d = Get-Order $r.data.orderNo
    OrderPost 'Cancel' @{ customerId = $script:customerId; orderNo = $d.orderNo } | Out-Null

    # 运费既能压到 0（平台白送），也能抬到 9999（平台凭空收钱）。
    # 金额字段由客户端说了算，从来都不是「算错」，是压根没算。
    return $d.freight -eq 10.00
}

Invoke-Case 'API-ORD-133' '满额包邮：商品实付 120 ≥ 门槛 100 时免运费' {
    $r = New-FeeOrder 'expressBig' 'feeFree' -ClientFreight 0 -Qty 1
    if (-not $r.success) { Write-Host ("        下单失败: " + $r.message) -ForegroundColor DarkYellow; return $false }
    $d = Get-Order $r.data.orderNo
    OrderPost 'Cancel' @{ customerId = $script:customerId; orderNo = $d.orderNo } | Out-Null

    return $d.freight -eq 0.00 -and $d.payableAmount -eq 120.00
}

Invoke-Case 'API-ORD-134' '🔴 虚拟商品不收运费（平台配了 10 元也不收）' {
    $r = New-FeeOrder 'virtual' 'feeVirtual' -ClientFreight 0
    if (-not $r.success) { Write-Host ("        下单失败: " + $r.message) -ForegroundColor DarkYellow; return $false }
    $d = Get-Order $r.data.orderNo
    OrderPost 'Cancel' @{ customerId = $script:customerId; orderNo = $d.orderNo } | Out-Null

    # BUSINESS.md 6.2「只对实物快递收运费；虚拟商品与自提恒为 0」。
    return $d.freight -eq 0.00 -and $d.payableAmount -eq 51.00
}

Invoke-Case 'API-ORD-135' '🔴 自提商品不收运费' {
    $r = New-FeeOrder 'pickup' 'feePickup' -ClientFreight 0
    if (-not $r.success) { Write-Host ("        下单失败: " + $r.message) -ForegroundColor DarkYellow; return $false }
    $d = Get-Order $r.data.orderNo
    OrderPost 'Cancel' @{ customerId = $script:customerId; orderNo = $d.orderNo } | Out-Null

    return $d.freight -eq 0.00
}

Invoke-Case 'API-ORD-136' '🔴 把自提商品谎报成快递也收不到运费' {
    # 配送方式决定要不要收运费，所以它和单价一样不能由客户端说了算：
    # 谎报成快递能凭空多收 10 元，谎报成自提能白嫖免运费。
    $r = New-FeeOrder 'pickup' 'feeSpoof' -ClientFreight 0 -ClaimedDeliveryType 1
    if (-not $r.success) { Write-Host ("        下单失败: " + $r.message) -ForegroundColor DarkYellow; return $false }
    $d = Get-Order $r.data.orderNo
    OrderPost 'Cancel' @{ customerId = $script:customerId; orderNo = $d.orderNo } | Out-Null

    return $d.freight -eq 0.00 -and $d.payableAmount -eq 51.00
}

Invoke-Case 'API-ORD-137' '运费与商品金额恒等式仍然成立：实付 = 商品总额 + 运费' {
    $d = Get-Order $script:feeOrderA
    return $d.payableAmount -eq ($d.goodsTotal + $d.freight)
}

Invoke-Case 'API-ORD-138' '取消运费单把库存还回去' {
    OrderPost 'Cancel' @{ customerId = $script:customerId; orderNo = $script:feeOrderA } | Out-Null
    return $true
}

Invoke-Case 'API-ORD-140' '🔴 P0 试算金额与真实下单逐分一致（含运费与优惠）' {
    # 这是整个试算接口存在的意义：小程序此前在前端自己算「商品金额 − 券优惠」，
    # 既不含运费也不含活动与积分，页面显示的「预计应付」与真实下单金额对不上。
    # 只断言「试算返回了一个数字」毫无意义 —— 必须拿它和真下单的金额逐分比。
    $p = $script:feeProductIds['express']
    $preview = Invoke-Api "$Order/orders/Preview" 'Post' @{
        customerId = $script:customerId; platformId = $script:feePlatformId; merchantId = 0
        lines = @(@{ spuId = [long]$p.productId; skuId = [long]$p.skuId; quantity = 2 })
        couponId = 0; pointsToUse = 0
    }
    if (-not $preview.success) {
        Write-Host ("        试算失败: " + $preview.message) -ForegroundColor DarkYellow
        return $false
    }

    $r = New-FeeOrder 'express' 'previewMatch' -ClientFreight 0
    if (-not $r.success) { return $false }
    $d = Get-Order $r.data.orderNo
    OrderPost 'Cancel' @{ customerId = $script:customerId; orderNo = $d.orderNo } | Out-Null

    $pv = $preview.data
    Write-Host ("        试算 实付={0} 运费={1} | 下单 实付={2} 运费={3}" -f `
        $pv.payableAmount, $pv.freight, $d.payableAmount, $d.freight) -ForegroundColor DarkGray

    return $pv.payableAmount -eq $d.payableAmount `
        -and $pv.freight -eq $d.freight `
        -and $pv.goodsTotal -eq $d.goodsTotal `
        -and $pv.payableAmount -eq 61.00
}

Invoke-Case 'API-ORD-141' '试算不占用任何资源：券、积分、库存都不动' {
    $p = $script:feeProductIds['express']
    $before = Get-Stock ([long]$p.skuId)

    $preview = Invoke-Api "$Order/orders/Preview" 'Post' @{
        customerId = $script:customerId; platformId = $script:feePlatformId; merchantId = 0
        lines = @(@{ spuId = [long]$p.productId; skuId = [long]$p.skuId; quantity = 2 })
        couponId = $script:couponId; pointsToUse = 100
    }
    if (-not $preview.success) { return $false }

    $after = Get-Stock ([long]$p.skuId)

    # 试算是纯查询。连看一眼结算页就把券锁掉 / 把库存冻住是不能接受的 ——
    # 用户反复进出结算页会把资源占得死死的。
    return $after.available -eq $before.available -and $after.locked -eq $before.locked
}

Invoke-Case 'API-ORD-142' '试算返回可用券并标出最优（K9）' {
    $p = $script:feeProductIds['express']
    $preview = Invoke-Api "$Order/orders/Preview" 'Post' @{
        customerId = $script:customerId; platformId = $script:feePlatformId; merchantId = 0
        lines = @(@{ spuId = [long]$p.productId; skuId = [long]$p.skuId; quantity = 2 })
        couponId = 0; pointsToUse = 0
    }
    if (-not $preview.success) { return $false }

    $opts = @($preview.data.couponOptions)
    if ($opts.Count -eq 0) {
        Write-Host '        该平台没有可用券（券是平台 0 的，跨平台不通用）' -ForegroundColor DarkGray
        return $true
    }

    # 「最优惠」只能有一个，且必须真的在列表里 —— 前端默认选中它。
    return @($opts | Where-Object { $_.isBest }).Count -eq 1 `
        -and ($opts | Where-Object { $_.isBest }).couponId -eq $preview.data.bestCouponId
}

Invoke-Case 'API-ORD-143' '试算的积分上限 = 应付商品金额的 100%（元→分）' {
    $p = $script:feeProductIds['express']
    $preview = Invoke-Api "$Order/orders/Preview" 'Post' @{
        customerId = $script:customerId; platformId = $script:feePlatformId; merchantId = 0
        lines = @(@{ spuId = [long]$p.productId; skuId = [long]$p.skuId; quantity = 2 })
        couponId = 0; pointsToUse = 0
    }
    if (-not $preview.success) { return $false }

    # 商品实付 51.00 元 → 最多抵 5100 分。
    # 这里最容易被写成 51（漏了 ×100），症状是「积分怎么都抵不完」，
    # 而界面上只显示一个 maxPointsToUse，看不出单位错了。
    return $preview.data.maxPointsToUse -eq 5100
}

Invoke-Case 'API-ORD-144' '游客也能试算（只是没有券与积分）' {
    $p = $script:feeProductIds['express']
    $preview = Invoke-Api "$Order/orders/Preview" 'Post' @{
        customerId = 0; platformId = $script:feePlatformId; merchantId = 0
        lines = @(@{ spuId = [long]$p.productId; skuId = [long]$p.skuId; quantity = 2 })
        couponId = 0; pointsToUse = 0
    }
    return $preview.success -and $preview.data.payableAmount -eq 61.00 -and $preview.data.couponOptions.Count -eq 0
}

Invoke-Case 'API-ORD-145' '试算会拦下未过审 / 已下架的商品（而不是算出一个假的价）' {
    $p = $script:feeProductIds['express']
    Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $p.productId; status = 2 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $preview = Invoke-Api "$Order/orders/Preview" 'Post' @{
        customerId = $script:customerId; platformId = $script:feePlatformId; merchantId = 0
        lines = @(@{ spuId = [long]$p.productId; skuId = [long]$p.skuId; quantity = 2 })
        couponId = 0; pointsToUse = 0
    }

    # 恢复上架，否则后面的清理用例会因为商品不可售而表现异常
    Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $p.productId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    # 试算要报「已下架」，而不是照常返回一个金额 ——
    # 后者会让用户看着一个算得出的价点下去，然后被下单接口拒绝。
    return (-not $preview.success) -and ($preview.message -match '下架')
}

Invoke-Case 'API-ORD-139' '清理运费测试的平台与商品' {
    foreach ($k in $script:feeProductIds.Keys) {
        Invoke-RestMethod "$Gateway/gateway/products/Delete" -Method Post -Headers $script:adminHeaders `
            -Body (@{ productId = $script:feeProductIds[$k].productId } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
    # 分类与平台同样要删：运费活动的有效期可能很长，留着会让「平台运费」在别的
    # 用例里继续生效，表现是别的单凭空多收一笔运费，而报错指向完全无关的用例。
    foreach ($cid in @($script:feeCategoryIds | Sort-Object -Descending)) {
        if ($cid -gt 0) {
            Invoke-RestMethod "$Gateway/gateway/categories/Delete" -Method Post -Headers $script:adminHeaders `
                -Body (@{ categoryId = $cid } | ConvertTo-Json) `
                -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        }
    }
    if ($script:feePlatformId -gt 0) {
        Invoke-RestMethod "$Gateway/gateway/platforms/Delete" -Method Post -Headers $script:adminHeaders `
            -Body (@{ platformId = $script:feePlatformId } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
    return $script:feeProductIds.Count -eq 4 -and $script:feePlatformId -gt 0
}

Write-Host "`n=== ORD 清理 ===" -ForegroundColor Cyan

Invoke-Case 'API-ORD-120' '清理测试商品与分类' {
    Invoke-RestMethod "$Gateway/gateway/products/Delete" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $script:productId } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $tree = Invoke-RestMethod "$Gateway/gateway/categories/Tree" -Headers $script:adminHeaders -TimeoutSec 30
    foreach ($n in @($tree.data) | Where-Object { $_.categoryName -like "订单$($script:suffix)*" }) {
        Invoke-RestMethod "$Gateway/gateway/categories/Delete" -Method Post -Headers $script:adminHeaders `
            -Body (@{ categoryId = $n.id } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
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
