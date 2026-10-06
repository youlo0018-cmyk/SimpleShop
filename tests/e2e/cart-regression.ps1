<#
.SYNOPSIS
    CartService（5060）回归测试。
.DESCRIPTION
    购物车的核心是一条容易搞反的语义：**Add 是累加，不是赋值**。
    传目标数量会出现倍数增长，而且症状很隐蔽（数量看着对，只是多了几倍）。

    对应 TEST_CASES：
      API-CART-001 P0 加购为累加语义（1+1+1 = 3，不是 6）
      API-CART-002 P1 加购上限 99
      API-CART-003 P1 加购带图会刷新图片快照
    另外覆盖：减到 0 移除该行、金额两位小数。
#>
[CmdletBinding()]
param(
    [string]$Cart = 'http://127.0.0.1:5060',
    [string]$Gateway = 'http://127.0.0.1:5008',
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
$script:customerId = 700000000 + $script:suffix
$script:skuId = 0
$script:price = 25.5

function Get-AdminToken {
    $d = [System.Collections.Generic.Dictionary[string,string]]::new()
    $d['grant_type'] = 'password'; $d['client_id'] = 'admin-app'
    $d['username'] = $AdminUser;     $d['password'] = $AdminPassword
    $r = $http.PostAsync("$Gateway/gateway/auth/token", [System.Net.Http.FormUrlEncodedContent]::new($d)).GetAwaiter().GetResult()
    return ($r.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).access_token
}

function CartPost([string]$Op, $Body) {
    return Invoke-RestMethod -Uri "$Cart/carts/$Op" -Method Post `
        -Body ($Body | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30
}

Write-Host "`n=== CART 准备：建一个带 SKU 的商品 ===" -ForegroundColor Cyan

Invoke-Case 'API-CRT-000' '准备商品与 SKU（后续用例依赖它）' {
    $headers = @{ Authorization = "Bearer $(Get-AdminToken)" }

    $c1 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $headers `
        -Body (@{ parentId = 0; categoryName = "购物车$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $c2 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $headers `
        -Body (@{ parentId = $c1; categoryName = "购物车$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $c3 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $headers `
        -Body (@{ parentId = $c2; categoryName = "购物车$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data

    $body = @{
        productId = 0; spuName = "购物车商品$($script:suffix)"; categoryId = $c3
        deliveryType = 1; mainImage = 'https://cdn.example.com/main.png'
        specs = @(@{ specName = '颜色'; specValues = @('红') })
        skus = @(@{ skuCode = "CART$($script:suffix)"; specValues = @('红'); price = $script:price; stock = 500; status = 1 })
    }
    $prodId = (Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $headers `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30).data

    # 🔴 购物车会按「审核通过 + 已上架」判定该行能不能买（与下单同一套口径），
    # 所以这里必须先把商品推到可售状态，否则下面每一条都会看到 isAvailable = false。
    Invoke-RestMethod "$Gateway/gateway/products/Audit" -Method Post -Headers $headers `
        -Body (@{ productId = $prodId; auditStatus = 20 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $headers `
        -Body (@{ productId = $prodId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $d = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$prodId" -Headers $headers -TimeoutSec 30
    $script:skuId = [long]$d.data.skus[0].id
    $script:productId = $prodId
    # 存下来：API-CRT-014 要靠改价来验证购物车会刷新单价，
    # 而改价走的是同一个 Save 接口，必须带上原来的分类。
    $script:categoryId = $c3
    return $script:skuId -gt 0
}

Write-Host "`n=== CART 累加语义（P0）===" -ForegroundColor Cyan

Invoke-Case 'API-CRT-001' '🔴 P0 连续加购 1、1、1 → 数量 = 3（**不是 6**）' {
    $a = CartPost 'Add' @{ customerId = $script:customerId; skuId = $script:skuId; delta = 1 }
    $b = CartPost 'Add' @{ customerId = $script:customerId; skuId = $script:skuId; delta = 1 }
    $c = CartPost 'Add' @{ customerId = $script:customerId; skuId = $script:skuId; delta = 1 }

    return $a.data.quantity -eq 1 -and $b.data.quantity -eq 2 -and $c.data.quantity -eq 3
}

Invoke-Case 'API-CRT-002' '小计 = 单价 × 数量，两位小数' {
    $r = CartPost 'Add' @{ customerId = $script:customerId; skuId = $script:skuId; delta = 1 }
    # 25.50 × 4 = 102.00
    return $r.data.quantity -eq 4 -and $r.data.subTotal -eq 102.00
}

Invoke-Case 'API-CRT-003' '同一 SKU 只有一行（不插新行）' {
    $list = Invoke-RestMethod "$Cart/carts/List?customerId=$($script:customerId)" -TimeoutSec 30
    return @($list.data).Count -eq 1
}

Write-Host "`n=== CART 减与上限 ===" -ForegroundColor Cyan

Invoke-Case 'API-CRT-004' '传 -1 是减 1（购物车加减号走同一入口）' {
    $r = CartPost 'Add' @{ customerId = $script:customerId; skuId = $script:skuId; delta = -1 }
    return $r.data.quantity -eq 3
}

Invoke-Case 'API-CRT-005' '🔴 减到 0 时移除该行（购物车不该有 0 件的条目）' {
    CartPost 'Add' @{ customerId = $script:customerId; skuId = $script:skuId; delta = -99 } | Out-Null
    $list = Invoke-RestMethod "$Cart/carts/List?customerId=$($script:customerId)" -TimeoutSec 30
    return @($list.data).Count -eq 0
}

Invoke-Case 'API-CRT-006' '🔴 超过上限 99 被拒' {
    CartPost 'Add' @{ customerId = $script:customerId; skuId = $script:skuId; delta = 99 } | Out-Null
    $r = CartPost 'Add' @{ customerId = $script:customerId; skuId = $script:skuId; delta = 1 }
    return (-not $r.success) -and $r.message -match '99'
}

Write-Host "`n=== CART 快照与设置 ===" -ForegroundColor Cyan

Invoke-Case 'API-CRT-007' '加购时刷新图片 / 名称快照' {
    $list = Invoke-RestMethod "$Cart/carts/List?customerId=$($script:customerId)" -TimeoutSec 30
    $item = @($list.data)[0]
    return $null -ne $item.skuName -and $item.skuSpecText -match '红' -and $item.price -eq $script:price
}

Invoke-Case 'API-CRT-008' '直接设数量（不是累加）' {
    $list = Invoke-RestMethod "$Cart/carts/List?customerId=$($script:customerId)" -TimeoutSec 30
    $id = @($list.data)[0].id
    CartPost 'SetQuantity' @{ customerId = $script:customerId; cartId = $id; quantity = 7 } | Out-Null

    $after = Invoke-RestMethod "$Cart/carts/List?customerId=$($script:customerId)" -TimeoutSec 30
    return @($after.data)[0].quantity -eq 7
}

Invoke-Case 'API-CRT-009' '设数量为 0 等于删除该行' {
    $list = Invoke-RestMethod "$Cart/carts/List?customerId=$($script:customerId)" -TimeoutSec 30
    $id = @($list.data)[0].id
    CartPost 'SetQuantity' @{ customerId = $script:customerId; cartId = $id; quantity = 0 } | Out-Null

    $after = Invoke-RestMethod "$Cart/carts/List?customerId=$($script:customerId)" -TimeoutSec 30
    return @($after.data).Count -eq 0
}

Invoke-Case 'API-CRT-010' '改别人的购物车行被拒（按 customerId 过滤）' {
    $other = $script:customerId + 1
    CartPost 'Add' @{ customerId = $script:customerId; skuId = $script:skuId; delta = 2 } | Out-Null
    $list = Invoke-RestMethod "$Cart/carts/List?customerId=$($script:customerId)" -TimeoutSec 30
    $id = @($list.data)[0].id

    # 用别人的 customerId 去改这一行
    CartPost 'SetQuantity' @{ customerId = $other; cartId = $id; quantity = 50 } | Out-Null
    $after = Invoke-RestMethod "$Cart/carts/List?customerId=$($script:customerId)" -TimeoutSec 30
    return @($after.data)[0].quantity -eq 2
}

Write-Host "`n=== CART 清理 ===" -ForegroundColor Cyan

Invoke-Case 'API-CRT-011' '清空购物车' {
    CartPost 'Clear' @{ customerId = $script:customerId } | Out-Null
    $list = Invoke-RestMethod "$Cart/carts/List?customerId=$($script:customerId)" -TimeoutSec 30
    return @($list.data).Count -eq 0
}

Write-Host "`n=== CART 价格与可售状态必须实时（不能停留在加购那一刻）===" -ForegroundColor Cyan

$script:headers = @{ Authorization = "Bearer $(Get-AdminToken)" }

Invoke-Case 'API-CRT-013' '准备：重新加购 1 件（API-CRT-011 已清空购物车）' {
    $r = CartPost 'Add' @{ customerId = $script:customerId; skuId = $script:skuId; delta = 1 }
    return $r.data.quantity -eq 1
}

Invoke-Case 'API-CRT-014' '🔴 P0 商家改价后，购物车显示新价（不必重新加购）' {
    # 快照此前只在**加购时**刷新一次。加完购去忙别的、商家此时把 25.50 改成 39.00，
    # 购物车里仍是 25.50 —— 而下单链路已经改成按权威售价算账，
    # 于是页面显示 25.50、实际扣 39.00，用户看不出差在哪。
    $save = @{
        productId = $script:productId; spuName = "购物车商品$($script:suffix)"; categoryId = $script:categoryId
        deliveryType = 1; mainImage = 'https://cdn.example.com/main.png'
        specs = @(@{ specName = '颜色'; specValues = @('红') })
        skus = @(@{ skuCode = "CART$($script:suffix)"; specValues = @('红'); price = 39.00; stock = 500; status = 1 })
    }
    Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($save | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $list = Invoke-RestMethod "$Cart/carts/List?customerId=$($script:customerId)" -TimeoutSec 30
    $row = @($list.data)[0]
    if (-not $row) { return $false }

    Write-Host ("        购物车单价={0} 小计={1}" -f $row.price, $row.subTotal) -ForegroundColor DarkGray

    # 39.00 × 1 = 39.00。仍然是 25.50 就说明快照没刷新。
    return $row.price -eq 39.00 -and $row.subTotal -eq 39.00
}

Invoke-Case 'API-CRT-015' '🔴 P0 商品下架后，该行标记为不可用且不参与勾选' {
    # 商品下架后，购物车里那一行既不能删（用户回头想看），也不该能结算
    # （点了会被下单接口拒）。标灰 + 不勾选是唯一诚实的处理：
    # 否则用户勾完全部去结算，页面给出一个总价，下单却报「商品已下架」。
    Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:productId; status = 2 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $list = Invoke-RestMethod "$Cart/carts/List?customerId=$($script:customerId)" -TimeoutSec 30
    $row = @($list.data)[0]
    if (-not $row) { return $false }

    # 恢复上架，否则后面两条用例会拿到一个已下架的商品。
    Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:productId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    Write-Host ("        isAvailable={0} reason={1} checked={2}" -f `
        $row.isAvailable, $row.unavailableReason, $row.checked) -ForegroundColor DarkGray

    return (-not $row.isAvailable) -and $row.unavailableReason -match '下架' -and (-not $row.checked)
}

Invoke-Case 'API-CRT-016' '恢复上架后重新变成可勾选（置灰不是单向的）' {
    $list = Invoke-RestMethod "$Cart/carts/List?customerId=$($script:customerId)" -TimeoutSec 30
    $row = @($list.data)[0]
    return $row.isAvailable -and $row.unavailableReason -eq ''
}

Invoke-Case 'API-CRT-012' '清理测试商品与分类' {
    $headers = @{ Authorization = "Bearer $(Get-AdminToken)" }
    Invoke-RestMethod "$Gateway/gateway/products/Delete" -Method Post -Headers $headers `
        -Body (@{ productId = $script:productId } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $tree = Invoke-RestMethod "$Gateway/gateway/categories/Tree" -Headers $headers -TimeoutSec 30
    $nodes = @($tree.data) | Where-Object { $_.categoryName -like "购物车$($script:suffix)*" }
    foreach ($n in $nodes) {
        Invoke-RestMethod "$Gateway/gateway/categories/Delete" -Method Post -Headers $headers `
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
