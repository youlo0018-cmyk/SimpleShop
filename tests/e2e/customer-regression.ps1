<#
.SYNOPSIS
    CustomerService（5280）C 端回归：资料 / 地址簿 / 收藏。
.DESCRIPTION
    这三块此前**只有表和仓储，没有任何接口**（BUSINESS.md 180 写着「资料、地址簿、收藏」），
    所以本脚本的每一条都真的打一次接口。

    重点在两处：
      1. 归属：C 端接口一律过 CustomerScope —— A 传 B 的 customerId 必须 403；
         A 拿 B 的地址 Id（用自己的 customerId）必须 404（等同于不存在，不泄露「这个 Id 真实存在」）。
      2. 默认地址的连带规则：第一条自动默认、设默认要清掉旧的、删默认要顶上新的。
         这三条漏一条，用户下次结算就会发现「一条都没预选」或「两条都是默认」。
    退出码非 0 即视为回归失败。
#>
[CmdletBinding()]
param(
    [string]$Gateway = 'http://127.0.0.1:5008',
    [string]$Inventory = 'http://127.0.0.1:5062',
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
$script:customerA = 0
$script:customerB = 0
$script:tokenA = ''
$script:tokenB = ''
$script:address1 = 0
$script:address2 = 0

function New-Customer([string]$tag, [int]$offset) {
    # 手机号 11 位、1[3-9] 开头，两个客户要用不同的号
    $tail = ([string]($script:suffix + $offset)).PadLeft(8, '0').Substring(0, 8)
    return Invoke-RestMethod "$Gateway/gateway/customers/Register" -Method Post -ContentType 'application/json' `
        -Body (@{
            customerName = "$tag$($script:suffix)"; password = 'Cus12345678'
            phone = "138$tail"; nickName = $tag
        } | ConvertTo-Json) -TimeoutSec 30
}

# 带客户令牌打网关；业务失败也要把响应体拿回来（本项目有两种失败形态：抛异常与 200+success=false）
function Post-As([string]$path, $body, [string]$token) {
    try {
        $headers = @{}
        if (-not [string]::IsNullOrWhiteSpace($token)) {
            $headers.Authorization = "Bearer $token"
        }
        $r = Invoke-RestMethod "$Gateway$path" -Method Post -Headers $headers `
            -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 6) -TimeoutSec 30
        return [pscustomobject]@{
            status = 200; success = $r.success; code = $r.code
            message = $r.message; data = $r.data; errors = $r.errors
        }
    } catch {
        $raw = $_.ErrorDetails.Message
        $code = -1; $message = $_.Exception.Message; $errors = $null
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            try {
                $parsed = $raw | ConvertFrom-Json
                $code = $parsed.code; $message = $parsed.message; $errors = $parsed.errors
            } catch { }
        }
        return [pscustomobject]@{
            status = [int]$_.Exception.Response.StatusCode; success = $false
            code = $code; message = $message; data = $null; errors = $errors
        }
    }
}

function Get-ErrorText($resp) {
    if ($null -eq $resp.errors) { return '' }
    return (@($resp.errors.PSObject.Properties | ForEach-Object { $_.Value }) -join ' ')
}

Write-Host "`n=== CUS 资料 / 地址簿 / 收藏 ===" -ForegroundColor Cyan

Invoke-Case 'API-CUS-000' '准备：经网关注册两个客户' {
    $a = New-Customer 'cusA' 0
    $b = New-Customer 'cusB' 7
    $script:customerA = [long]$a.data.customerId
    $script:customerB = [long]$b.data.customerId
    $script:tokenA = [string]$a.data.token
    $script:tokenB = [string]$b.data.token
    return $script:customerA -gt 0 -and $script:customerB -gt 0 -and $script:customerA -ne $script:customerB
}

Invoke-Case 'API-CUS-000a' '缺必填字段时 MVC 模型绑定返回中文提示（不再回英文 required）' {
    $r = Post-As '/gateway/customers/Login' @{ customerName = "cus$($script:suffix)" } ''
    $text = Get-ErrorText $r
    return $r.status -eq 400 -and $text -match '不能为空' -and $text -notmatch 'required'
}

Invoke-Case 'API-CUS-000b' 'FluentValidation 链式规则的默认文案也是中文' {
    $r = Post-As '/gateway/customers/Login' @{ customerName = "cus$($script:suffix)"; password = '' } ''
    $text = Get-ErrorText $r
    return $r.status -eq 400 -and $text -match '[\u4e00-\u9fa5]' -and $text -notmatch 'required'
}

Invoke-Case 'API-CUS-000c' '客户令牌可上传评价图片（复用统一上传接口）' {
    $temp = Join-Path $env:TEMP "simpleshop-customer-upload-$($script:suffix).txt"
    Set-Content -LiteralPath $temp -Value 'customer evaluation image placeholder' -Encoding UTF8
    try {
        $r = Invoke-RestMethod "$Gateway/gateway/files/Upload" -Method Post `
            -Headers @{ Authorization = "Bearer $($script:tokenA)" } `
            -Form @{ file = Get-Item -LiteralPath $temp } -TimeoutSec 30
        return $r.success -and -not [string]::IsNullOrWhiteSpace($r.data.publicUrl)
    } finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    }
}

Invoke-Case 'API-CUS-000d' '🔴 客户 B 用 A 的订单申请退款 → 403（不能替别人退）' {
    # 找一个有库存的上架商品，用客户 A 建一张待支付订单；客户 B 拿订单号申请退款必须被拒。
    # 这条专门守住「退款申请也走 PaymentOwnership」——退款入口只有订单号，订单号是可枚举的。
    $products = Invoke-RestMethod "$Gateway/gateway/shop/products/List" -Method Post `
        -ContentType 'application/json' `
        -Body (@{ customerId = '0'; page = 1; pageSize = 20 } | ConvertTo-Json) -TimeoutSec 30

    $order = $null
    foreach ($item in @($products.data.items)) {
        $detail = Invoke-RestMethod "$Gateway/gateway/shop/products/Detail" -Method Post `
            -ContentType 'application/json' `
            -Body (@{ customerId = $script:customerA; productId = $item.productId } | ConvertTo-Json) -TimeoutSec 30
        # 前台详情只返回启用中的 SKU（停用 SKU 已在服务端过滤），这里直接取第一条。
        $sku = @($detail.data.skus)[0]
        if (-not $sku) { continue }

        $snapshot = Invoke-RestMethod "$Inventory/internal/inventory/Snapshot?skuIds=$($sku.skuId)" -TimeoutSec 20
        if ([int]$snapshot.data[0].available -lt 1) { continue }

        $body = @{
            customerId = $script:customerA; platformId = 0; merchantId = 0
            idempotencyKey = "CUS-REFUND-$($script:suffix)"
            receiverName = '越权测试'; receiverPhone = '13800000000'; receiverAddress = '某地 1 号楼 101'
            lines = @(@{
                spuId = $item.productId; skuId = $sku.skuId; quantity = 1
                unitPrice = $sku.finalPrice; productName = $sku.skuName
                skuSpecText = $sku.skuSpecText; deliveryType = $detail.data.deliveryType
            })
            couponId = 0; pointsToUse = 0; freight = 0
        }
        $created = Post-As '/gateway/orders/Create' $body $script:tokenA
        if ($created.success) { $order = $created.data; break }
    }

    if ($null -eq $order) {
        Write-Host '        找不到有库存的上架商品，无法构造退款越权用例' -ForegroundColor DarkYellow
        return $false
    }

    $refund = Post-As '/gateway/refunds/Apply' @{
        orderId = $order.orderId; orderNo = $order.orderNo
        items = @(); reason = '越权退款测试'
    } $script:tokenB

    # 业务失败是 HTTP 200 + code=403（与全项目一致），不能断言 HTTP 状态码。
    return $refund.code -eq 403 -and $refund.message -match '其他客户'
}

Invoke-Case 'API-CUS-001' '查自己的资料：手机号打码下发' {
    $r = Post-As '/gateway/customers/Profile' @{ customerId = $script:customerA } $script:tokenA
    if (-not $r.success) { Write-Host ("        实际返回：" + $r.message) -ForegroundColor DarkYellow; return $false }

    # 完整手机号没必要到处传：C 端展示用打码的就够，泄露面小一圈
    return $r.data.customerId -eq [string]$script:customerA `
        -and $r.data.phone -match '^\d{3}\*{4}\d{4}$' `
        -and $r.data.genderName -ne ''
}

Invoke-Case 'API-CUS-002' '改自己的资料（昵称 / 头像 / 性别 / 生日）后能查回来' {
    $r = Post-As '/gateway/customers/UpdateProfile' @{
        customerId = $script:customerA; nickName = '新昵称'; avatar = 'https://cdn.example.com/a.png'
        gender = 2; birthday = '1995-06-15T00:00:00Z'
    } $script:tokenA
    if (-not $r.success) { Write-Host ("        实际返回：" + $r.message) -ForegroundColor DarkYellow; return $false }

    $back = Post-As '/gateway/customers/Profile' @{ customerId = $script:customerA } $script:tokenA
    return $back.data.nickName -eq '新昵称' `
        -and $back.data.gender -eq 2 -and $back.data.genderName -eq '女' `
        -and $back.data.birthday -eq '1995-06-15'
}

Invoke-Case 'API-CUS-003' '改资料校验：空昵称 / 非法性别都被拒' {
    $empty = Post-As '/gateway/customers/UpdateProfile' @{
        customerId = $script:customerA; nickName = ''; gender = 0
    } $script:tokenA
    $badGender = Post-As '/gateway/customers/UpdateProfile' @{
        customerId = $script:customerA; nickName = '合法昵称'; gender = 9
    } $script:tokenA

    return (-not $empty.success) -and (Get-ErrorText $empty) -match '昵称' `
        -and (-not $badGender.success) -and (Get-ErrorText $badGender) -match '性别'
}

Invoke-Case 'API-CUS-004' '新增第一条地址：自动成为默认地址' {
    $r = Post-As '/gateway/customers/addresses/Create' @{
        customerId = $script:customerA; consigneeName = '张三'; consigneePhone = '13800000001'
        provinceCode = '330000'; cityCode = '330100'; districtCode = '330106'
        regionPath = '浙江省/杭州市/西湖区'; detailAddress = '文一西路 1 号'; isDefault = $false
        label = '家'
    } $script:tokenA
    if (-not $r.success) { Write-Host ("        实际返回：" + $r.message) -ForegroundColor DarkYellow; return $false }
    $script:address1 = [long]$r.data

    $list = Post-As '/gateway/customers/addresses/List' @{ customerId = $script:customerA } $script:tokenA
    $first = @($list.data.items)[0]
    # 一条默认都没有的话，结算页不知道预选哪个 —— 第一条必须自动顶上
    return $first.isDefault -eq $true -and $first.regionPath -eq '浙江省/杭州市/西湖区'
}

Invoke-Case 'API-CUS-005' '新增第二条并设默认：旧默认被自动清掉（不会出现两条默认）' {
    $r = Post-As '/gateway/customers/addresses/Create' @{
        customerId = $script:customerA; consigneeName = '李四'; consigneePhone = '13800000002'
        provinceCode = '310000'; cityCode = '310100'; districtCode = '310104'
        regionPath = '上海市/上海市/徐汇区'; detailAddress = '宜山路 2 号'; isDefault = $true
        label = '公司'
    } $script:tokenA
    if (-not $r.success) { Write-Host ("        实际返回：" + $r.message) -ForegroundColor DarkYellow; return $false }
    $script:address2 = [long]$r.data

    $list = Post-As '/gateway/customers/addresses/List' @{ customerId = $script:customerA } $script:tokenA
    $defaults = @($list.data.items | Where-Object { $_.isDefault -eq $true })
    $new = @($list.data.items | Where-Object { [long]$_.addressId -eq $script:address2 })[0]
    return $defaults.Count -eq 1 -and $new.isDefault -eq $true
}

Invoke-Case 'API-CUS-006' '地址校验：收货手机号格式不对被拒' {
    $r = Post-As '/gateway/customers/addresses/Create' @{
        customerId = $script:customerA; consigneeName = '王五'; consigneePhone = '12345'
        provinceCode = '110000'; cityCode = '110100'; districtCode = '110101'
        regionPath = '北京市/北京市/东城区'; detailAddress = '某街 3 号'
    } $script:tokenA

    return (-not $r.success) -and (Get-ErrorText $r) -match '手机号'
}

Invoke-Case 'API-CUS-007' '改地址：改完能查回来' {
    $r = Post-As '/gateway/customers/addresses/Update' @{
        customerId = $script:customerA; addressId = $script:address1
        consigneeName = '张三改'; consigneePhone = '13800000003'
        provinceCode = '330000'; cityCode = '330100'; districtCode = '330106'
        regionPath = '浙江省/杭州市/西湖区'; detailAddress = '文一西路 99 号'; isDefault = $false
        label = '家'
    } $script:tokenA
    if (-not $r.success) { Write-Host ("        实际返回：" + $r.message) -ForegroundColor DarkYellow; return $false }

    $list = Post-As '/gateway/customers/addresses/List' @{ customerId = $script:customerA } $script:tokenA
    $hit = @($list.data.items | Where-Object { [long]$_.addressId -eq $script:address1 })[0]
    return $hit.consigneeName -eq '张三改' -and $hit.detailAddress -eq '文一西路 99 号'
}

Invoke-Case 'API-CUS-008' '删掉默认地址：剩下最新的一条自动顶上默认' {
    $r = Post-As '/gateway/customers/addresses/Delete' @{
        customerId = $script:customerA; addressId = $script:address2
    } $script:tokenA
    if (-not $r.success) { Write-Host ("        实际返回：" + $r.message) -ForegroundColor DarkYellow; return $false }

    $list = Post-As '/gateway/customers/addresses/List' @{ customerId = $script:customerA } $script:tokenA
    $items = @($list.data.items)
    $defaults = @($items | Where-Object { $_.isDefault -eq $true })
    return $items.Count -eq 1 -and $defaults.Count -eq 1 -and [long]$defaults[0].addressId -eq $script:address1
}

Invoke-Case 'API-CUS-009' '收藏商品 → 列表可见；重复收藏幂等' {
    $spu = 900000000 + $script:suffix
    $first = Post-As '/gateway/customers/favorites/Add' @{ customerId = $script:customerA; spuId = $spu } $script:tokenA
    $again = Post-As '/gateway/customers/favorites/Add' @{ customerId = $script:customerA; spuId = $spu } $script:tokenA

    $list = Post-As '/gateway/customers/favorites/List' @{ customerId = $script:customerA } $script:tokenA
    $hit = @($list.data.items | Where-Object { [long]$_.spuId -eq $spu })

    # 商品服务里查不到这个测试 SPU：接口要保留收藏记录，并把可购买状态回成 false，
    # 而不是让整个收藏页 500（P2-23 的降级路径）。
    return $first.success -and $again.success -and $list.data.total -eq 1 -and $hit.Count -eq 1 `
        -and $hit[0].available -eq $false
}

Invoke-Case 'API-CUS-010' '收藏上限 20：第 21 件被拒（额度不足）' {
    $spuBase = 910000000 + $script:suffix
    $rejected = $null
    for ($i = 1; $i -le 20; $i++) {
        $r = Post-As '/gateway/customers/favorites/Add' @{
            customerId = $script:customerA; spuId = ($spuBase + $i)
        } $script:tokenA
        if (-not $r.success) { $rejected = $r; break }
    }

    # 前 19 件应该都成功（第 1 件是上一条用例收藏的），第 21 件撞上限
    $list = Post-As '/gateway/customers/favorites/List' @{ customerId = $script:customerA; pageSize = 50 } $script:tokenA
    return $null -ne $rejected -and $rejected.code -eq 4004 -and $list.data.total -eq 20
}

Invoke-Case 'API-CUS-011' '取消收藏：没收藏过也返回成功（幂等）' {
    $spu = 920000000 + $script:suffix
    $r = Post-As '/gateway/customers/favorites/Remove' @{ customerId = $script:customerA; spuId = $spu } $script:tokenA
    return $r.success
}

Invoke-Case 'API-CUS-012' '🔴 客户 A 用客户 B 的 Id 查资料 / 地址 / 收藏 → 403' {
    $profile = Post-As '/gateway/customers/Profile' @{ customerId = $script:customerB } $script:tokenA
    $address = Post-As '/gateway/customers/addresses/List' @{ customerId = $script:customerB } $script:tokenA
    $favorite = Post-As '/gateway/customers/favorites/List' @{ customerId = $script:customerB } $script:tokenA

    return $profile.status -eq 403 -and $profile.code -eq 403 `
        -and $address.status -eq 403 -and $favorite.status -eq 403
}

Invoke-Case 'API-CUS-013' '🔴 客户 A 拿 B 的地址 Id 去改 / 删 → 404（等同于不存在，不泄露 Id 是否真实）' {
    # B 自己建一条地址，A 拿这个 Id 去改：A 用自己的 customerId，所以不是 403，
    # 而是被客户 AOP 过滤成「查不到」→ 404。
    $created = Post-As '/gateway/customers/addresses/Create' @{
        customerId = $script:customerB; consigneeName = 'B 的收货人'; consigneePhone = '13800000009'
        provinceCode = '440000'; cityCode = '440100'; districtCode = '440106'
        regionPath = '广东省/广州市/天河区'; detailAddress = '某路 9 号'
    } $script:tokenB
    if (-not $created.success) { return $false }
    $bAddress = [long]$created.data

    $update = Post-As '/gateway/customers/addresses/Update' @{
        customerId = $script:customerA; addressId = $bAddress
        consigneeName = '越权改'; consigneePhone = '13800000010'
        provinceCode = '440000'; cityCode = '440100'; districtCode = '440106'
        regionPath = '广东省/广州市/天河区'; detailAddress = '越权改的地址'
    } $script:tokenA
    $delete = Post-As '/gateway/customers/addresses/Delete' @{
        customerId = $script:customerA; addressId = $bAddress
    } $script:tokenA

    Write-Host ("        改={0} 删={1}" -f $update.code, $delete.code) -ForegroundColor DarkGray
    return (-not $update.success) -and $update.code -eq 404 `
        -and (-not $delete.success) -and $delete.code -eq 404
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
