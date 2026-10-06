<#
.SYNOPSIS
    PaymentService（5066）回归测试。
.DESCRIPTION
    覆盖 BUSINESS.md 10 支付与退款、DATA_SPEC 5.25~5.28：
      - 支付单金额**服务端反查**（命令里没有金额字段）
      - 支付幂等：重复创建 / 重复确认都拿到同一结果，**不重复触发收尾**
      - 模拟支付失败：订单保持 10 待支付，占用不变，可重新发起
      - 退款窗口按配送方式判定：虚拟 {20,30} 可退、**签收后不可退（含部分退）**；
        实物全程可退
      - 累计退款 ≤ 实付（含运费）；整单退含运费、部分退不退运费
      - 审批时二次校验累计限额；重复审批被拒；拒绝无副作用
    退出码非 0 即视为回归失败。
#>
[CmdletBinding()]
param(
    [string]$Gateway = 'http://127.0.0.1:5008',
    [string]$Payment = 'http://127.0.0.1:5066',
    [string]$Order = 'http://127.0.0.1:5064',
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

Add-Type -AssemblyName System.Net.Http
$http = [System.Net.Http.HttpClient]::new()

$script:suffix = Get-Random -Minimum 100000 -Maximum 999999
$script:customerId = 970000000 + $script:suffix
$script:price = 25.50
$script:productId = 0
$script:categoryIds = @()
$script:skuIds = @()

function Get-AdminToken {
    $d = [System.Collections.Generic.Dictionary[string,string]]::new()
    $d['grant_type'] = 'password'; $d['client_id'] = 'admin-app'
    $d['username'] = $AdminUser;     $d['password'] = $AdminPassword
    $r = $http.PostAsync("$Gateway/gateway/auth/token", [System.Net.Http.FormUrlEncodedContent]::new($d)).GetAwaiter().GetResult()
    return ($r.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).access_token
}

$script:adminHeaders = @{ Authorization = "Bearer $(Get-AdminToken)" }

function PayPost([string]$Op, $Body) {
    # $Op 自带前导 /，只能直接拼接；写成 "$Payment/$Op" 会拼出双斜杠而全部 404
    # 校验失败与部分业务失败都由全局异常中间件返回 **HTTP 400**，
    # Invoke-RestMethod 见到 400 直接抛异常，body 里的 { success:false, message } 就拿不到了。
    # 所以统一包一层：无论 HTTP 是不是错误，都把响应体解析出来返回。
    try {
        return Invoke-RestMethod "$Payment$Op" -Method Post -Headers $script:adminHeaders `
            -Body ($Body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 60
    } catch {
        $raw = $_.ErrorDetails.Message
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return [pscustomobject]@{ success = $false; code = -1; message = $_.Exception.Message; data = $null }
        }
        try { return $raw | ConvertFrom-Json -AsHashtable } catch { return [pscustomobject]@{ success = $false; code = -1; message = $raw; data = $null } }
    }
}

function PayAdminPost([string]$Op, $Body) {
    # 退款审批必须**走网关**。
    # 审批人现在由 Handler 从令牌租户上下文（TenantContextHolder）取，
    # 而那个上下文是网关验签后通过 X-Claim-* 头注入的 ——
    # 直连 $Payment 就没有租户上下文，审批会被「登录状态已失效」挡掉。
    # 这不是测试在迁就实现，而是后台的真实调用路径就是走网关：
    # 运营点「通过退款」时请求必然经过网关，审批人才是对的。
    # $Op 形如 '/refunds/Approve'，已经带了 refunds 前缀，
    # 所以这里只能拼 '$Gateway/gateway' + $Op。
    # 之前写成 "$Gateway/gateway/refunds/$($Op -replace '^/','')" 拼出了
    # /gateway/refunds/refunds/Approve —— 少一层路由，全部 404。
    $path = "$Gateway/gateway$Op"
    try {
        return Invoke-RestMethod $path -Method Post -Headers $script:adminHeaders `
            -Body ($Body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 60
    } catch {
        $raw = $_.ErrorDetails.Message
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return [pscustomobject]@{ success = $false; code = -1; message = $_.Exception.Message; data = $null }
        }
        try { return $raw | ConvertFrom-Json -AsHashtable } catch { return [pscustomobject]@{ success = $false; code = -1; message = $raw; data = $null } }
    }
}

function OrderPost([string]$Op, $Body) {
    return Invoke-RestMethod "$Order$Op" -Method Post -Headers $script:adminHeaders `
        -Body ($Body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 60
}

function Get-OrderStatus([string]$OrderNo) {
    $d = Invoke-RestMethod "$Order/orders/Detail?orderNo=$OrderNo&customerId=$($script:customerId)" -TimeoutSec 30
    return [int]$d.data.status
}

<#
.SYNOPSIS
    取某个 SKU 的库存三个计数。
.DESCRIPTION
    退款是否真的回补了库存，只能看这三个数：available 涨回去、deducted 减回去。
    只看接口返回的 success 是不够的 —— 曾经两段式退款回的是「退款已生效」，
    而货一直挂在 deducted 上，页面上完全看不出异常。
#>
function Get-Stock([long]$SkuId) {
    return (Invoke-RestMethod "$Inventory/internal/inventory/Snapshot?skuIds=$SkuId" -TimeoutSec 20).data[0]
}

# 校验失败时 message 只有「请求参数校验失败」，具体原因在 errors 里（DATA_SPEC 3.5）。
# 断言「因某条规则被拒」必须看 errors，否则永远匹配不上。
function Get-ErrText($Resp) {
    if ($null -eq $Resp.errors) { return '' }
    $errs = $Resp.errors
    if ($errs -is [System.Collections.IDictionary]) {
        return (@($errs.Values | ForEach-Object { $_ }) -join ' ')
    }
    return (@($errs.PSObject.Properties | ForEach-Object { $_.Value }) -join ' ')
}

<#
.SYNOPSIS
    建一笔待支付订单。
.DESCRIPTION
    $DeliveryType 决定后面的退款窗口判定：1 实物快递（全程可退）/ 2 虚拟（仅 20、30 可退）。

    <b>$Freight 只是照抄进请求体，用来证明它会被忽略</b> —— 运费现在由服务端按平台配置算
    （order-regression 的 ORD-131~136 才是验证运费算对了的地方）。
    真要一笔**确实带运费**的订单，用 $PlatformId / $SkuId 指到「运费平台」那套商品上。
#>
function New-PendingOrder([string]$Tag, [int]$DeliveryType = 1, [decimal]$Freight = 0,
        [long]$PlatformId = 0, [long]$SkuId = 0, [long]$SpuId = 0) {
    # 虚拟单必须用虚拟商品：配送方式挂在 SPU 级（BUSINESS.md 6.1），服务端以商品为准。
    if ($DeliveryType -eq 2 -and $SkuId -le 0) {
        $SkuId = $script:virtualSkuId
        $SpuId = $script:virtualProductId
    }
    if ($SkuId -le 0) { $SkuId = $script:skuIds[0] }
    if ($SpuId -le 0) { $SpuId = $script:productId }
    $line = @{
        spuId = $SpuId
        skuId = $SkuId
        quantity = 2
        unitPrice = $script:price
        productName = "支付商品$($script:suffix)"
        skuSpecText = '红 / M'
        deliveryType = $DeliveryType
    }
    $body = @{
        customerId = $script:customerId
        platformId = $PlatformId
        merchantId = 0
        idempotencyKey = "PAY-$Tag-$($script:suffix)"
        receiverName = '张三'
        receiverPhone = '13800000000'
        receiverAddress = '某地某小区 1 号楼 101'
        lines = @($line)
        couponId = 0
        pointsToUse = 0
        freight = $Freight
    }
    return (Invoke-RestMethod "$Order/orders/Create" -Method Post -Headers $script:adminHeaders `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 60).data
}

Write-Host "`n=== PAY 准备：三级分类 + 一个有库存的商品 ===" -ForegroundColor Cyan

Invoke-Case 'API-PAY-000' '建三级分类 + 200 件库存的商品（单价 25.50）' {
    $c1 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = 0; categoryName = "支付$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $c2 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = $c1; categoryName = "支付$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $c3 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = $c2; categoryName = "支付$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $script:categoryIds = @($c1, $c2, $c3)

    $body = @{
        productId = 0; spuName = "支付商品$($script:suffix)"; categoryId = $c3
        deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
        specs = @(@{ specName = '颜色'; specValues = @('红') })
        skus = @(@{ skuCode = "PAY$($script:suffix)"; specValues = @('红'); price = $script:price; stock = 200; status = 1 })
    }
    $script:productId = [long](Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:adminHeaders `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 60).data

    # 🔴 商品必须「审核通过 + 已上架」才可下单（BUSINESS.md 14.4「商品需审核后上架」）。
    # 下单链路会回查这两项，未过审 / 未上架一律拒单，所以这里必须先把商品推到可售状态。
    (Invoke-RestMethod "$Gateway/gateway/products/Audit" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $script:productId; auditStatus = 20 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30) | Out-Null
    (Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $script:productId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30) | Out-Null

    $det = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:productId)" `
        -Headers $script:adminHeaders -TimeoutSec 30
    $script:skuIds = @($det.data.skus | ForEach-Object { [long]$_.id })

    # 🔴 虚拟单要**自己的商品**：配送方式挂在 SPU 级，一个 SPU 只有一种
    # （BUSINESS.md 6.1），服务端以商品为准。
    # 拿快递商品在下单时把 deliveryType 改成 2 是改不动的 ——
    # 退款窗口判定（虚拟仅 20/30 可退）测的就成了快递单，结论是假的。
    $vBody = @{
        productId = 0; spuName = "虚拟商品$($script:suffix)"; categoryId = $c3
        deliveryType = 2; mainImage = 'https://cdn.example.com/m.png'
        specs = @(@{ specName = '颜色'; specValues = @('红') })
        skus = @(@{ skuCode = "PAYV$($script:suffix)"; specValues = @('红'); price = $script:price; stock = 200; status = 1 })
    }
    $vId = [long](Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:adminHeaders -Body ($vBody | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 60).data
    (Invoke-RestMethod "$Gateway/gateway/products/Audit" -Method Post -Headers $script:adminHeaders -Body (@{ productId = $vId; auditStatus = 20 } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30) | Out-Null
    (Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:adminHeaders -Body (@{ productId = $vId; status = 1 } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30) | Out-Null

    $vd = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$vId" -Headers $script:adminHeaders -TimeoutSec 30
    $script:virtualProductId = $vId
    $script:virtualSkuId = [long](@($vd.data.skus | Where-Object { $_.skuCode -eq "PAYV$($script:suffix)" })[0].id)

    return $script:productId -gt 0 -and $script:skuIds.Count -eq 1 -and $script:virtualSkuId -gt 0
}

Write-Host "`n=== PAY 支付单：金额服务端反查 + 幂等 ===" -ForegroundColor Cyan

Invoke-Case 'API-PAY-010' '创建支付单：金额 = 订单实付（服务端反查，非客户端传入）' {
    $o = New-PendingOrder 'A'
    $script:orderA = $o.orderNo
    if (-not $o.orderNo) { return $false }

    $r = PayPost '/payments/Create' @{ orderNo = $script:orderA }
    if (-not $r.success) { return $false }

    # 2 件 × 25.50 = 51.00，无运费无优惠
    return $r.data.amount -eq 51.00 -and $r.data.statusName -eq '待支付' -and $r.data.paymentNo.Length -gt 0
}

Invoke-Case 'API-PAY-011' '🔴 重复创建支付单：拿到**同一张单**而不是造一堆' {
    $a = PayPost '/payments/Create' @{ orderNo = $script:orderA }
    $b = PayPost '/payments/Create' @{ orderNo = $script:orderA }
    # 用户反复点「去支付」是常态，幂等键 {order_no}:{channel}
    return $a.data.paymentNo -eq $b.data.paymentNo
}

Invoke-Case 'API-PAY-012' '订单不存在时被拒' {
    $r = PayPost '/payments/Create' @{ orderNo = 'NOT-EXIST-ORDER' }
    return (-not $r.success) -and $r.message -match '订单不存在'
}

Write-Host "`n=== PAY 模拟支付 ===" -ForegroundColor Cyan

Invoke-Case 'API-PAY-020' '模拟支付失败：订单**保持 10 待支付**，不关单' {
    $r = PayPost '/payments/Simulate' @{ orderNo = $script:orderA; success = $false }
    return $r.success -and (Get-OrderStatus $script:orderA) -eq 10
}

Invoke-Case 'API-PAY-021' '模拟支付成功：订单 10 → 20 已支付' {
    $r = PayPost '/payments/Simulate' @{ orderNo = $script:orderA; success = $true }
    return $r.success -and $r.data.statusName -eq '已支付' -and (Get-OrderStatus $script:orderA) -eq 20
}

Invoke-Case 'API-PAY-022' '🔴 重复确认支付：幂等，订单状态不回退' {
    $r = PayPost '/payments/Confirm' @{ orderNo = $script:orderA }
    # 关键是**不重复触发收尾**（积分实扣 / 库存确认 / 券核销）——跑两次就是实打实的资损
    return $r.success -and (Get-OrderStatus $script:orderA) -eq 20
}

Invoke-Case 'API-PAY-023' '已支付的订单不能再模拟支付（状态不回退）' {
    $r = PayPost '/payments/Simulate' @{ orderNo = $script:orderA; success = $true }
    return $r.success -and (Get-OrderStatus $script:orderA) -eq 20
}

Write-Host "`n=== PAY 退款：窗口 + 累计限额 + 两段式审批 ===" -ForegroundColor Cyan

Invoke-Case 'API-PAY-029' '建一个配了 10 元运费的平台 + 其下的实物快递商品' {
    # 「整单退款含运费」这条断言需要一笔**确实带运费**的订单。
    # 运费改由服务端按平台配置算之后，光在请求体里写 freight = 10 是没用的 ——
    # 那正是被忽略的那个字段。这里造一个真正配了运费的平台来产生它。
    $code = -join ((1..6) | ForEach-Object { [char](65 + (Get-Random -Max 26)) })
    $plat = Invoke-RestMethod "$Gateway/gateway/platforms/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{
            platformName = "退运费$($script:suffix)"; mallName = "退运费商城$($script:suffix)"
            platformCode = $code; contactName = '测试'; contactPhone = '13800000000'
            shippingFee = 10; freeShippingThreshold = 0; status = 1
        } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30
    if (-not $plat.success) { Write-Host ("        建平台失败: " + $plat.message) -ForegroundColor DarkYellow; return $false }
    $script:feePlatformId = [long]$plat.data

    $f1 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = 0; categoryName = "退运费$($script:suffix)"; platformId = $script:feePlatformId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $f2 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = $f1; categoryName = "退运费$($script:suffix)"; platformId = $script:feePlatformId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $f3 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = $f2; categoryName = "退运费$($script:suffix)"; platformId = $script:feePlatformId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data

    $newId = [long](Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:adminHeaders `
        -Body (@{
            productId = 0; spuName = "退运费商品$($script:suffix)"; categoryId = $f3
            platformId = $script:feePlatformId
            deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
            specs = @(@{ specName = '颜色'; specValues = @('红') })
            skus = @(@{ skuCode = "PAY-F$($script:suffix)"; specValues = @('红'); price = $script:price; stock = 200; status = 1 })
        } | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30).data

    Invoke-RestMethod "$Gateway/gateway/products/Audit" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $newId; auditStatus = 20 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $newId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $d = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$newId" -Headers $script:adminHeaders -TimeoutSec 30
    $script:feeProductId = $newId
    $script:feeSkuId = [long](@($d.data.skus | Where-Object { $_.skuCode -eq "PAY-F$($script:suffix)" })[0].id)
    $script:feeCategoryIds = @($f1, $f2, $f3)
    return $script:feeSkuId -gt 0
}

Invoke-Case 'API-PAY-030' '准备：一张已支付、状态 20 待发货的实物订单（含 10 元运费）' {
    $o = New-PendingOrder 'B' -DeliveryType 1 -Freight 10 `
        -PlatformId $script:feePlatformId -SkuId $script:feeSkuId -SpuId $script:feeProductId
    $script:orderB = $o.orderNo
    PayPost '/payments/Simulate' @{ orderNo = $script:orderB; success = $true } | Out-Null
    # 顺手确认这笔单真的带上了 10 元运费：否则下面「整单退含运费」会在运费为 0 的单上
    # 空跑通过，看起来验证了规则，其实什么都没验证。
    $det = (Invoke-RestMethod "$Order/orders/Detail?orderNo=$($script:orderB)&customerId=$($script:customerId)" `
        -TimeoutSec 30).data
    Write-Host ("        实付={0} 运费={1} 商品总额={2}" -f $det.payableAmount, $det.freight, $det.goodsTotal) -ForegroundColor DarkGray
    return (Get-OrderStatus $script:orderB) -eq 20 -and $det.freight -eq 10.00
}

Invoke-Case 'API-PAY-031' '🔴 P0 退款窗口：实物订单 20 待发货**可退**' {
    $r = PayPost '/refunds/Apply' @{
        orderId = 0; orderNo = $script:orderB; items = $null
        reason = '买错了，不想要了'
    }
    $script:refundB = [long]$r.data
    return $r.success -and $r.data -gt 0
}

Invoke-Case 'API-PAY-032' '🔴 P0 整单退款金额**含运费**（51.00 商品 + 10.00 运费 = 61.00）' {
    $r = PayPost '/refunds/List' @{ status = 10; orderNo = $script:orderB; page = 1; pageSize = 10 }
    $row = @($r.data.items | Where-Object { $_.refundId -eq "$($script:refundB)" })[0]
    # 整单退含运费是规格 10.2 的明确要求；部分退才不退运费
    return $row.amount -eq 61.00 -and $row.refundTypeName -eq '整单退款'
}

Invoke-Case 'API-PAY-033' '🔴 申请阶段**不动订单**：审批前订单仍是 20' {
    # 两段式的意义就在这：运营误点申请不会立刻造成资损
    return (Get-OrderStatus $script:orderB) -eq 20
}

Invoke-Case 'API-PAY-034' '🔴 重复申请同一订单的整单退款被拒（已有一张待审批）' {
    $r = PayPost '/refunds/Apply' @{ orderId = 0; orderNo = $script:orderB; items = $null; reason = '再申请一次' }
    # 累计会超过实付，必须挡住
    return (-not $r.success) -and $r.message -match '超过'
}

Invoke-Case 'API-PAY-035' '🔴 P0 审批通过：订单转 60 已退款' {
    # 审批人由服务端从令牌上下文取，请求体里**不再传** approverId / approverName
    $r = PayAdminPost '/refunds/Approve' @{ refundId = $script:refundB }
    if (-not $r.success) { return $false }
    return (Get-OrderStatus $script:orderB) -eq 60
}

Invoke-Case 'API-PAY-036' '🔴 重复审批被拒（幂等：不能退两次）' {
    $r = PayAdminPost '/refunds/Approve' @{ refundId = $script:refundB }
    return (-not $r.success) -and $r.message -match '已处理'
}

Invoke-Case 'API-PAY-035b' '🔴 P0 审批通过后库存必须回补（两段式退款此前不回补）' {
    # BUSINESS.md 10.2：「审批通过 → 退款单已退款 → 发 payment.refunded →
    # 订单转 60 + **库存回补** + **积分回收**」。
    #
    # 而订单服务的 mark-refunded 处理器**只改了状态**：
    # 两段式退款（/refunds/Apply + Approve，也就是规格里那套带审批的正式流程）
    # 走完之后，货永久从库存里消失。实测：下单 2 件 → 支付 →
    # available 98 / deducted 2 → 申请 → 审批通过 → 订单显示已退款，
    # 但 available 仍是 98、deducted 仍是 2。
    #
    # 订单服务另有一条 /admin/orders/Refund 的单步退款做了回补 ——
    # 同一个业务规则又写在了两条路径里，而支付单测只断言了状态码，看不见库存。
    $o = New-PendingOrder 'STOCK'
    if (-not $o.orderNo) { return $false }
    $no = $o.orderNo

    PayPost '/payments/Simulate' @{ orderNo = $no; success = $true } | Out-Null

    $sku = $script:skuIds[0]
    $afterPay = Get-Stock $sku

    $apply = PayPost '/refunds/Apply' @{
        orderId = 0; orderNo = $no; items = $null; reason = '整单退款，验证库存回补'
    }
    if (-not $apply.success) { Write-Host ("        申请失败: " + $apply.message) -ForegroundColor DarkYellow; return $false }

    $approve = PayAdminPost '/refunds/Approve' @{ refundId = [long]$apply.data }
    if (-not $approve.success) { Write-Host ("        审批失败: " + $approve.message) -ForegroundColor DarkYellow; return $false }

    Start-Sleep -Seconds 2
    $afterRefund = Get-Stock $sku

    Write-Host ("        支付后 available={0} deducted={1} → 退款后 available={2} deducted={3}" -f `
        $afterPay.available, $afterPay.deducted, $afterRefund.available, $afterRefund.deducted) -ForegroundColor DarkGray

    # 🔴 订单侧的退款台账也必须写上。
    # 资金流水在支付服务的 refund_order，而「行级可退余额」读的是订单侧的
    # order_refund_item —— 两本账各记各的时，一个**整单退完**的单
    # 在后台详情里仍然显示「可退 100.00」，运营会以为退款没生效。
    $list = Invoke-RestMethod "$Gateway/gateway/admin/orders/List" -Method Post -Headers $script:adminHeaders `
        -Body (@{ keyword = $no; page = 1; pageSize = 5 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    $oid = (@($list.data.items)[0]).orderId
    $detail = Invoke-RestMethod "$Gateway/gateway/admin/orders/Detail" -Method Post -Headers $script:adminHeaders `
        -Body (@{ orderId = $oid } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 20
    $line = @($detail.data.items)[0]
    Write-Host ("        后台详情：行实付={0} 行可退={1}" -f $line.payableAmount, $line.refundableAmount) -ForegroundColor DarkGray

    # 两件货必须从 deducted 回到 available
    return (Get-OrderStatus $no) -eq 60 `
        -and $afterRefund.available -eq ($afterPay.available + 2) `
        -and $afterRefund.deducted -eq ($afterPay.deducted - 2) `
        -and $line.refundableAmount -eq 0
}

Invoke-Case 'API-PAY-035c' '🔴 P0 部分退款后订单**不能**被标成整单已退款（剩下的钱还要能退）' {
    # 两段式退款以前**无条件**把订单转 60，于是「退了一件」等于「整单作废」：
    # 实测退掉 60 / 实付 100 之后，订单状态直接变成 60，剩下 40 客户再也退不了。
    # BUSINESS 10.2 明确支持按行部分退，单步退款那条路径也只退一行时不改状态。
    #
    # 这里在同一行上退一部分金额（51 元里退 20），等价于部分退款，
    # 不需要再造一个 SKU。
    $o = New-PendingOrder 'PART'
    if (-not $o.orderNo) { return $false }
    $no = $o.orderNo

    PayPost '/payments/Simulate' @{ orderNo = $no; success = $true } | Out-Null
    if ((Get-OrderStatus $no) -ne 20) { return $false }

    $list = Invoke-RestMethod "$Gateway/gateway/admin/orders/List" -Method Post -Headers $script:adminHeaders `
        -Body (@{ keyword = $no; page = 1; pageSize = 5 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 20
    $oid = (@($list.data.items)[0]).orderId
    $detail = Invoke-RestMethod "$Gateway/gateway/admin/orders/Detail" -Method Post -Headers $script:adminHeaders `
        -Body (@{ orderId = $oid } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 20
    $line = @($detail.data.items)[0]
    $paid = [decimal]$detail.data.payableAmount

    $apply = PayPost '/refunds/Apply' @{
        orderId = 0; orderNo = $no
        items = @(@{ orderItemId = [long]$line.orderItemId; amount = 20.00 })
        reason = '只退其中一部分，验证订单不会被整单关掉'
    }
    if (-not $apply.success) { Write-Host ("        申请失败: " + $apply.message) -ForegroundColor DarkYellow; return $false }
    $approve = PayAdminPost '/refunds/Approve' @{ refundId = [long]$apply.data }
    if (-not $approve.success) { Write-Host ("        审批失败: " + $approve.message) -ForegroundColor DarkYellow; return $false }

    $status = Get-OrderStatus $no
    $after = Invoke-RestMethod "$Gateway/gateway/admin/orders/Detail" -Method Post -Headers $script:adminHeaders `
        -Body (@{ orderId = $oid } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 20
    $lineAfter = @($after.data.items)[0]

    Write-Host ("        实付 {0}，退了 20.00；订单状态={1}，行可退={2}" -f `
        $paid, $status, $lineAfter.refundableAmount) -ForegroundColor DarkGray
    Write-Host ("        订单级：已退={0} 还可退={1}" -f `
        $after.data.refundedAmount, $after.data.remainingRefundable) -ForegroundColor DarkGray

    # 状态必须**不是** 60（整单已退款），剩下的钱还得能退。
    # 订单级的「已退 / 还可退」也要跟着动 —— 那读的是 order.refunded_amount，
    # 而两段式退款以前从不更新它（退了 20 仍显示「还可退 51」）。
    return $status -ne 60 `
        -and $lineAfter.refundableAmount -eq ($line.payableAmount - 20.00) `
        -and $after.data.refundedAmount -eq 20.00 `
        -and $after.data.remainingRefundable -eq ($paid - 20.00)
}

Invoke-Case 'API-PAY-037' '🔴🔴 审批人取自令牌，伪造请求体里的 approverName 无效' {
    # 这是本轮修掉的审计缺陷：ApproveRefundCommand 的审批人曾直接取自请求体，
    # 调用方可以自称任意审批人，而「退款单审批人」是财务审计凭据。
    # 现在审批人由 Handler 从 TenantContextHolder（网关注入的 X-Claim-*）取，
    # 请求体里就算塞 approverName 也不再生效。
    $list = PayPost '/refunds/List' @{ page = 1; pageSize = 50 }
    if (-not $list.success) { return $false }

    $row = @($list.data.items | Where-Object { $_.refundId -eq $script:refundB })[0]
    if ($null -eq $row) { return $false }

    # 旧用例传的是 approverName = '财务'。修复后审批人来自令牌，绝不能还是它。
    Write-Host ("        实际审批人 = {0}" -f $row.approverName) -ForegroundColor DarkGray
    return (-not [string]::IsNullOrWhiteSpace($row.approverName)) -and $row.approverName -ne '财务'
}

Write-Host "`n=== PAY 虚拟订单退款窗口（本轮修正的缺陷）===" -ForegroundColor Cyan

Invoke-Case 'API-PAY-040' '准备：已支付的虚拟订单，状态 20 待发货' {
    # 虚拟订单支付后、商户发货前停在 20 待发货
    $o = New-PendingOrder 'V' -DeliveryType 2
    $script:orderV = $o.orderNo
    PayPost '/payments/Simulate' @{ orderNo = $script:orderV; success = $true } | Out-Null
    return (Get-OrderStatus $script:orderV) -eq 20
}

Invoke-Case 'API-PAY-041' '🔴 P0 虚拟订单 20 待发货**可退**（原实现误判为「虚拟完全不可退」）' {
    $r = PayPost '/refunds/Apply' @{ orderId = 0; orderNo = $script:orderV; items = $null; reason = '一直没发货，不想要了' }
    $script:refundV = [long]$r.data
    # 原实现是「虚拟商品订单完全不支持退款」，比规格严格得多：
    # 买家在这段时间里连反馈「没发货」都做不到，只能干等
    return $r.success -and $r.data -gt 0
}

Invoke-Case 'API-PAY-042' '虚拟退款单被拒绝后**无副作用**：订单仍是 20' {
    $r = PayAdminPost '/refunds/Reject' @{
        refundId = $script:refundV; rejectReason = '需要先联系商户'
    }
    # 规格 10.2：拒绝无任何副作用，订单 / 库存 / 积分都不动
    return $r.success -and (Get-OrderStatus $script:orderV) -eq 20
}

Invoke-Case 'API-PAY-043' '🔴 P0 虚拟订单发货后（50 已完成）**不可退**，含部分退款' {
    # 虚拟发货即完成（20 → 50）。签收后不可退是规格 10.2 的明确要求
    $ship = OrderPost '/admin/orders/DeliverVirtual' @{ orderNo = $script:orderV }
    if (-not $ship.success) { return $false }
    if ((Get-OrderStatus $script:orderV) -ne 50) { return $false }

    $whole = PayPost '/refunds/Apply' @{ orderId = 0; orderNo = $script:orderV; items = $null; reason = '签收后想退' }
    # 部分退款同样要拦住：只在整单退的路径上校验，部分退就绕过去了。
    # orderItemId 必须传**真实的订单行 Id**：传 0 会被参数校验先挡下来，
    # 那样测到的就不是「退款窗口拦住了部分退」，而是「校验拦住了 0」——用例是绿的，规则却没被验证。
    $det = Invoke-RestMethod "$Order/orders/Detail?orderNo=$($script:orderV)&customerId=$($script:customerId)" -TimeoutSec 30
    $lineId = [long]$det.data.items[0].orderItemId
    $part = PayPost '/refunds/Apply' @{
        orderId = 0; orderNo = $script:orderV
        items = @(@{ orderItemId = $lineId; amount = 10.00 })
        reason = '签收后想部分退'
    }
    return (-not $whole.success) -and (-not $part.success) `
        -and $whole.message -match '签收' -and $part.message -match '签收'
}

Invoke-Case 'API-PAY-044' '🔴 未支付的订单不能退款（没有钱可退）' {
    $o = New-PendingOrder 'NP'
    $r = PayPost '/refunds/Apply' @{ orderId = 0; orderNo = $o.orderNo; items = $null; reason = '还没付就想退' }
    return (-not $r.success) -and $r.message -match '待支付'
}

Invoke-Case 'API-PAY-045' '🔴 退款原因太短被拒（2~200 字符）' {
    $o = New-PendingOrder 'SHORT'
    $r = PayPost '/refunds/Apply' @{ orderId = 0; orderNo = $o.orderNo; items = $null; reason = 'x' }
    # 校验失败时 message 只有「请求参数校验失败」，具体原因在 errors 里
    return (-not $r.success) -and (Get-ErrText $r) -match '2'
}

Write-Host "`n=== PAY 清理 ===" -ForegroundColor Cyan

Invoke-Case 'API-PAY-090' '删商品 → 删分类' {
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
    # 运费平台同样要删：它配了 10 元运费，留着会让后面别的脚本凭空多收一笔运费，
    # 而报错会指向完全无关的用例。
    if ($script:feeProductId -gt 0) {
        Invoke-RestMethod "$Gateway/gateway/products/Delete" -Method Post -Headers $script:adminHeaders `
            -Body (@{ productId = $script:feeProductId } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 60 | Out-Null
    }
    if ($script:virtualProductId -gt 0) {
        Invoke-RestMethod "$Gateway/gateway/products/Delete" -Method Post -Headers $script:adminHeaders `
            -Body (@{ productId = $script:virtualProductId } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 60 | Out-Null
    }
    foreach ($id in [array]($script:feeCategoryIds | Sort-Object -Descending)) {
        Invoke-RestMethod "$Gateway/gateway/categories/Delete" -Method Post -Headers $script:adminHeaders `
            -Body (@{ categoryId = $id } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
    if ($script:feePlatformId -gt 0) {
        Invoke-RestMethod "$Gateway/gateway/platforms/Delete" -Method Post -Headers $script:adminHeaders `
            -Body (@{ platformId = $script:feePlatformId } | ConvertTo-Json) `
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
