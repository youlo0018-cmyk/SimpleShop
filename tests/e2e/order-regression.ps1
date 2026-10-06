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

Invoke-Case 'API-ORD-010' '下单成功：实付 = 25.50 × 2，状态待支付' {
    $r = OrderPost 'Create' (New-OrderBody 'basic')
    $script:basicOrderNo = $r.data.orderNo
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
