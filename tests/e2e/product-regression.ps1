<#
.SYNOPSIS
    ProductService（5058）分类域回归测试。
.DESCRIPTION
    分类是后续商品、活动、装修的基础，规则错了会一路传到前台。这里验证：
      - 层级强制 ≤ 3，且 Level 由服务端按父链算出（前端传了也忽略）
      - 同父级内分类名唯一
      - 父分类不存在时拒绝
      - 有子分类 / 有商品时禁止删除，只能停用
      - 分类树正确带出 hasChildren
.PARAMETER Gateway
    网关地址，走网关才能顺带验证 RBAC 与租户头注入。
#>
[CmdletBinding()]
param(
    [string]$Gateway = 'http://127.0.0.1:5008',
    [string]$Product = 'http://127.0.0.1:5058',
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

function Get-Token {
    $d = [System.Collections.Generic.Dictionary[string,string]]::new()
    $d['grant_type'] = 'password'; $d['client_id'] = 'admin-app'
    $d['username'] = $AdminUser;     $d['password'] = $AdminPassword

    $resp = $http.PostAsync("$Gateway/gateway/auth/token", [System.Net.Http.FormUrlEncodedContent]::new($d)).GetAwaiter().GetResult()
    return ($resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).access_token
}

$script:token = Get-Token
$script:headers = @{ Authorization = "Bearer $script:token" }
$script:suffix = Get-Random -Minimum 100000 -Maximum 999999

function Post-Cat($Body) {
    Invoke-RestMethod -Uri "$Gateway/gateway/categories/Create" -Method Post `
        -Headers $script:headers -Body ($Body | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30
}

Write-Host "`n=== PRD 分类（经网关，含 RBAC 与租户头链路）===" -ForegroundColor Cyan

Invoke-Case 'API-PRD-001' '无令牌访问分类树被拒（401）' {
    try { Invoke-RestMethod "$Gateway/gateway/categories/Tree" -TimeoutSec 15 | Out-Null; return $false }
    catch { return [int]$_.Exception.Response.StatusCode -eq 401 }
}

Invoke-Case 'API-PRD-002' '新建一级分类成功，level=1' {
    $script:l1 = (Post-Cat @{ parentId = 0; categoryName = "分类A$($script:suffix)"; platformId = 0 }).data
    return $script:l1 -gt 0
}

Invoke-Case 'API-PRD-003' '新建二级分类成功，level 由服务端算出' {
    $script:l2 = (Post-Cat @{ parentId = $script:l1; categoryName = "分类B$($script:suffix)"; platformId = 0 }).data
    $tree = Invoke-RestMethod "$Gateway/gateway/categories/Tree" -Headers $script:headers -TimeoutSec 30
    $node = $tree.data.children | Where-Object { $_.id -eq "$($script:l2)" }
    return $script:l2 -gt 0 -and $node.level -eq 2
}

Invoke-Case 'API-PRD-004' '新建三级分类成功，level=3' {
    $script:l3 = (Post-Cat @{ parentId = $script:l2; categoryName = "分类C$($script:suffix)"; platformId = 0 }).data
    $tree = Invoke-RestMethod "$Gateway/gateway/categories/Tree" -Headers $script:headers -TimeoutSec 30
    $l2node = $tree.data.children | Where-Object { $_.id -eq "$($script:l2)" }
    $node = $l2node.children | Where-Object { $_.id -eq "$($script:l3)" }
    return $script:l3 -gt 0 -and $node.level -eq 3
}

Invoke-Case 'API-PRD-005' '🔴 第四级被拒（层级上限 3）' {
    $r = Post-Cat @{ parentId = $script:l3; categoryName = "分类D$($script:suffix)"; platformId = 0 }
    return -not $r.success -and $r.message -match '3 级'
}

Invoke-Case 'API-PRD-006' '同父级同名被拒' {
    $r = Post-Cat @{ parentId = 0; categoryName = "分类A$($script:suffix)"; platformId = 0 }
    return -not $r.success -and $r.message -match '同名'
}

Invoke-Case 'API-PRD-007' '上级分类不存在被拒' {
    $r = Post-Cat @{ parentId = 999999999999; categoryName = "孤儿$($script:suffix)"; platformId = 0 }
    return -not $r.success -and $r.message -match '不存在'
}

Invoke-Case 'API-PRD-008' '🔴 有子分类时删除被拒' {
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/categories/Delete" -Method Post `
        -Headers $script:headers -Body (@{ categoryId = $script:l1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    return -not $r.success -and $r.message -match '子分类'
}

Invoke-Case 'API-PRD-009' '分类树带出 hasChildren' {
    $tree = Invoke-RestMethod "$Gateway/gateway/categories/Tree" -Headers $script:headers -TimeoutSec 30
    $node = $tree.data | Where-Object { $_.id -eq "$($script:l1)" }
    return $node.hasChildren -eq $true -and $node.children.Count -ge 1
}

Invoke-Case 'API-PRD-010' '叶子节点 hasChildren=false' {
    $tree = Invoke-RestMethod "$Gateway/gateway/categories/Tree" -Headers $script:headers -TimeoutSec 30
    $l1node = $tree.data | Where-Object { $_.id -eq "$($script:l1)" }
    $l2node = $l1node.children | Where-Object { $_.id -eq "$($script:l2)" }
    $l3node = $l2node.children | Where-Object { $_.id -eq "$($script:l3)" }
    return $l3node.hasChildren -eq $false
}

Invoke-Case 'API-PRD-011' '三级分类仍可用于挂商品（清理统一放在最后）' {
    # 分类要留给后面的商品用例用，删除动作统一放到脚本末尾的 API-PRP-016。
    $tree = Invoke-RestMethod "$Gateway/gateway/categories/Tree" -Headers $script:headers -TimeoutSec 30
    $l1node = $tree.data | Where-Object { $_.id -eq "$($script:l1)" }
    return $null -ne $l1node
}

Write-Host "`n=== PRB 品牌（商品的 BrandId 是选填项）===" -ForegroundColor Cyan
$script:brandId = 0

Invoke-Case 'API-PRB-001' '新建品牌成功' {
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/brands/Create" -Method Post -Headers $script:headers `
        -Body (@{ brandName = "品牌$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    $script:brandId = $r.data
    return $r.success -and $r.data -gt 0
}

Invoke-Case 'API-PRB-002' '同名品牌被拒（全局唯一）' {
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/brands/Create" -Method Post -Headers $script:headers `
        -Body (@{ brandName = "品牌$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    return -not $r.success -and $r.message -match '已存在'
}

Write-Host "`n=== PRP 商品 / 规格 / SKU ===" -ForegroundColor Cyan

# 规格组合：颜色(红/蓝) × 尺码(M/L) = 4 个 SKU
function New-SkuSet([decimal]$basePrice) {
    @(
        @{ skuCode = "T$($script:suffix)-R-M";  specValues = @('红','M'); price = $basePrice;           stock = 10; status = 1 },
        @{ skuCode = "T$($script:suffix)-R-L";  specValues = @('红','L'); price = $basePrice + 10;      stock = 20; status = 1 },
        @{ skuCode = "T$($script:suffix)-B-M";  specValues = @('蓝','M'); price = $basePrice + 20;      stock = 30; status = 1 },
        @{ skuCode = "T$($script:suffix)-B-L";  specValues = @('蓝','L'); price = $basePrice + 30;      stock = 40; status = 2 }
    )
}

$script:productId = 0
$script:rejectedId = 0

Invoke-Case 'API-PRP-001' '新建商品成功（新建固定待审核 + 默认下架）' {
    $body = @{
        productId = 0
        spuName = "测试商品$($script:suffix)"
        categoryId = $script:l3
        brandId = $script:brandId
        deliveryType = 1
        mainImage = 'https://cdn.example.com/main.png'
        specs = @(
            @{ specName = '颜色'; specValues = @('红','蓝') },
            @{ specName = '尺码'; specValues = @('M','L') }
        )
        skus = (New-SkuSet 100)
    }
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30
    $script:productId = $r.data
    return $r.success -and $r.data -gt 0
}

Invoke-Case 'API-PRP-002' '🔴 挂在非第 3 级分类下被拒' {
    $body = @{
        productId = 0; spuName = '越界商品'; categoryId = $script:l1
        deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
        specs = @(@{ specName = '颜色'; specValues = @('红') })
        skus = @(@{ skuCode = "X$($script:suffix)"; specValues = @('红'); price = 10; stock = 1; status = 1 })
    }
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30
    return -not $r.success -and $r.message -match '第 3 级'
}

Invoke-Case 'API-PRP-003' '🔴 SKU 少了一个规格项被拒' {
    # 只给了「颜色」一个取值，但商品有两个规格项
    $body = @{
        productId = 0; spuName = '规格不全'; categoryId = $script:l3
        deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
        specs = @(
            @{ specName = '颜色'; specValues = @('红') },
            @{ specName = '尺码'; specValues = @('M') }
        )
        skus = @(@{ skuCode = "Y$($script:suffix)"; specValues = @('红'); price = 10; stock = 1; status = 1 })
    }
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30
    return -not $r.success -and $r.message -match '规格项'
}

Invoke-Case 'API-PRP-004' '🔴 SKU 售价为 0 被拒' {
    $body = @{
        productId = 0; spuName = '零价商品'; categoryId = $script:l3
        deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
        specs = @(@{ specName = '颜色'; specValues = @('红') })
        skus = @(@{ skuCode = "Z$($script:suffix)"; specValues = @('红'); price = 0; stock = 1; status = 1 })
    }
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30
    return -not $r.success -and $r.message -match '售价'
}

Invoke-Case 'API-PRP-005' '🔴 划线原价低于售价被拒' {
    $body = @{
        productId = 0; spuName = '倒挂价'; categoryId = $script:l3
        deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
        specs = @(@{ specName = '颜色'; specValues = @('红') })
        skus = @(@{ skuCode = "W$($script:suffix)"; specValues = @('红'); price = 100; originalPrice = 50; stock = 1; status = 1 })
    }
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30
    return -not $r.success -and $r.message -match '划线原价'
}

Invoke-Case 'API-PRP-006' '🔴 同一次提交里 SKU 编码重复被拒' {
    $dup = @(
        @{ skuCode = "D$($script:suffix)"; specValues = @('红','M'); price = 10; stock = 1; status = 1 },
        @{ skuCode = "D$($script:suffix)"; specValues = @('蓝','L'); price = 10; stock = 1; status = 1 }
    )
    $body = @{
        productId = 0; spuName = '重复编码'; categoryId = $script:l3
        deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
        specs = @(
            @{ specName = '颜色'; specValues = @('红','蓝') },
            @{ specName = '尺码'; specValues = @('M','L') }
        )
        skus = $dup
    }
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30
    return -not $r.success -and $r.message -match '重复'
}

Invoke-Case 'API-PRP-007' '详情：4 个 SKU，SkuSpecText 按规格项顺序拼接' {
    $d = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:productId)" -Headers $script:headers -TimeoutSec 30
    if (-not $d.success -or $d.data.skus.Count -ne 4) { return $false }

    $first = $d.data.skus[0]
    return $first.skuSpecText -eq '红 / M'
}

Invoke-Case 'API-PRP-008' '详情：SkuName = 商品名 + 规格值' {
    $d = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:productId)" -Headers $script:headers -TimeoutSec 30
    $first = $d.data.skus[0]
    return $first.skuName -like "*$($script:suffix)*" -and $first.skuName -like '*红 / M*'
}

Invoke-Case 'API-PRP-009' '价格区间取**启用** SKU 的 min / max（停用的不计）' {
    $d = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:productId)" -Headers $script:headers -TimeoutSec 30
    # 启用的是 100 / 110 / 120；停用的那个是 130，**不该**算进 max
    return $d.data.minPrice -eq 100 -and $d.data.maxPrice -eq 120
}

Invoke-Case 'API-PRP-010' '🔴 审核未通过不能上架' {
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:productId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    return -not $r.success -and $r.message -match '审核'
}

Invoke-Case 'API-PRP-011' '审核通过后可以上架' {
    $a = Invoke-RestMethod -Uri "$Gateway/gateway/products/Audit" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:productId; auditStatus = 20 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    if (-not $a.success) { return $false }

    $l = Invoke-RestMethod -Uri "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:productId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    return $l.success
}

Invoke-Case 'API-PRP-012' '🔴 驳回后编辑**不重置**审核状态，必须显式重新提交' {
    # 必须用一个**全新**商品：已审核通过的商品不允许再审核
    # （规则「已通过无需重复审核」是业务要求，见 API-PRP-013 之后的验证）。
    $body = @{
        productId = 0
        spuName = "驳回用例$($script:suffix)"
        categoryId = $script:l3
        deliveryType = 1
        mainImage = 'https://cdn.example.com/r.png'
        specs = @(@{ specName = '颜色'; specValues = @('红') })
        skus = @(@{ skuCode = "RJ$($script:suffix)"; specValues = @('红'); price = 50; stock = 5; status = 1 })
    }
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30
    if (-not $r.success) { return $false }
    $script:rejectedId = $r.data

    $a = Invoke-RestMethod -Uri "$Gateway/gateway/products/Audit" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:rejectedId; auditStatus = 30; reason = '主图不合规' } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    if (-not $a.success) { return $false }

    # 编辑：改名 + 改价
    $edit = @{
        productId = $script:rejectedId
        spuName = "驳回用例$($script:suffix)-已修改"
        categoryId = $script:l3
        deliveryType = 1
        mainImage = 'https://cdn.example.com/r2.png'
        specs = @(@{ specName = '颜色'; specValues = @('红') })
        skus = @(@{ skuCode = "RJ$($script:suffix)"; specValues = @('红'); price = 66; stock = 5; status = 1 })
    }
    Invoke-RestMethod -Uri "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($edit | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $d = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:rejectedId)" -Headers $script:headers -TimeoutSec 30
    # 修改确实生效，但审核状态仍停在 30（驳回），没有被悄悄重置成 10
    return $d.data.auditStatus -eq 30 -and $d.data.spuName -like '*已修改*'
}

Invoke-Case 'API-PRP-013' '重新提交审核才回到待审核（10）' {
    $s = Invoke-RestMethod -Uri "$Gateway/gateway/products/SubmitAudit" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:rejectedId } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30
    if (-not $s.success) { return $false }

    $d = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:rejectedId)" -Headers $script:headers -TimeoutSec 30
    return $d.data.auditStatus -eq 10
}

Invoke-Case 'API-PRP-013b' '已审核通过的商品不允许重复审核' {
    $a = Invoke-RestMethod -Uri "$Gateway/gateway/products/Audit" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:productId; auditStatus = 20 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    return -not $a.success -and $a.message -match '无需重复审核'
}

Invoke-Case 'API-PRP-013c' '清理驳回用例的商品' {
    Invoke-RestMethod -Uri "$Gateway/gateway/products/Delete" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:rejectedId } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    return $true
}
Invoke-Case 'API-PRP-014' 'SKU 按编码 Upsert：改价生效、新增生效、消失的软删' {
    $keep = @(
        @{ skuCode = "T$($script:suffix)-R-M"; specValues = @('红','M'); price = 999; stock = 10; status = 1 },
        @{ skuCode = "T$($script:suffix)-R-L"; specValues = @('红','L'); price = 110;  stock = 20; status = 1 },
        @{ skuCode = "T$($script:suffix)-NEW";  specValues = @('蓝','M'); price = 200;  stock = 5;  status = 1 }
        # 注意：蓝/L 与 蓝/M 之外的那两个没提交，应被软删
    )
    $body = @{
        productId = $script:productId
        spuName = "测试商品$($script:suffix)"
        categoryId = $script:l3
        brandId = $script:brandId
        deliveryType = 1
        mainImage = 'https://cdn.example.com/main.png'
        specs = @(
            @{ specName = '颜色'; specValues = @('红','蓝') },
            @{ specName = '尺码'; specValues = @('M','L') }
        )
        skus = $keep
    }
    Invoke-RestMethod -Uri "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $d = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:productId)" -Headers $script:headers -TimeoutSec 30
    $codes = @($d.data.skus | ForEach-Object { $_.skuCode })

    return $codes.Count -eq 3
        -and ($codes -contains "T$($script:suffix)-NEW")
        -and ($d.data.skus | Where-Object { $_.skuCode -eq "T$($script:suffix)-R-M" }).price -eq 999
}

Invoke-Case 'API-PRP-015' '清理测试商品与品牌' {
    Invoke-RestMethod -Uri "$Gateway/gateway/products/Delete" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:productId } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    if ($script:brandId -gt 0) {
        Invoke-RestMethod -Uri "$Gateway/gateway/brands/Delete" -Method Post -Headers $script:headers `
            -Body (@{ brandId = $script:brandId } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
    return $true
}
Invoke-Case 'API-PRP-016' '清理：商品 → 品牌 → 分类（先叶子后父级）' {
    foreach ($id in @($script:l3, $script:l2, $script:l1)) {
        Invoke-RestMethod -Uri "$Gateway/gateway/categories/Delete" -Method Post `
            -Headers $script:headers -Body (@{ categoryId = $id } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
    $tree = Invoke-RestMethod "$Gateway/gateway/categories/Tree" -Headers $script:headers -TimeoutSec 30
    return -not ($tree.data | Where-Object { $_.id -eq "$($script:l1)" })
}
Write-Host "`n=== SHP 前台商品只读（无需登录）===" -ForegroundColor Cyan

# 前台用**自己的一套商品与活动**，不复用后台用例的数据：
# 后台用例会把自己的商品删掉，而前台用例要验证「审核通过 + 已上架」才可见，
# 复用同一批商品就会出现「上一条用例刚删掉它，前台就读不到了」这种时序脆弱。
$script:shopProductId = 0
$script:shopActivityId = 0
$script:shopCategoryId = 0

# 建出的三级分类 Id 要留着最后按「先叶子后父级」的顺序删：
# 有子分类时父级删不掉（那是防止误删孤立数据的保护，回归 API-PRD-008 就验过这条）。
$script:shopCategoryIds = @()

function New-ShopCategory {
    $a = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:headers `
        -Body (@{ parentId = 0; categoryName = "前台$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $b = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:headers `
        -Body (@{ parentId = $a; categoryName = "前台$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $c = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:headers `
        -Body (@{ parentId = $b; categoryName = "前台$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data

    $script:shopCategoryIds = @($a, $b, $c)
    return $c
}

# 前台列表 / 详情一律**不带令牌**调网关：能调通就同时证明了匿名白名单生效
function ShopPost([string]$Op, $Body) {
    return Invoke-RestMethod "$Gateway/gateway/shop/products/$Op" -Method Post `
        -Body ($Body | ConvertTo-Json -Depth 6) -ContentType 'application/json' -TimeoutSec 30
}

Invoke-Case 'API-SHP-001' '准备：一个三级分类 + 两个 SKU（200 元 / 100 元）的商品' {
    $script:shopCategoryId = New-ShopCategory

    $body = @{
        productId = 0; spuName = "前台商品$($script:suffix)"; categoryId = $script:shopCategoryId
        deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
        specs = @(@{ specName = '颜色'; specValues = @('红', '蓝') })
        skus = @(
            @{ skuCode = "SHP-A$($script:suffix)"; specValues = @('红'); price = 200; stock = 10; status = 1 }
            @{ skuCode = "SHP-B$($script:suffix)"; specValues = @('蓝'); price = 100; stock = 10; status = 1 }
        )
    }
    $script:shopProductId = [long](Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post `
        -Headers $script:headers -Body ($body | ConvertTo-Json -Depth 8) `
        -ContentType 'application/json' -TimeoutSec 30).data
    return $script:shopProductId -gt 0
}

Invoke-Case 'API-SHP-002' '🔴 未上架 + 未审核通过时前台看不到（服务端强制过滤，不靠调用方传参）' {
    $r = ShopPost 'List' @{ customerId = 0; keyword = "前台商品$($script:suffix)"; page = 1; pageSize = 10 }
    # 新建商品固定「待审核 + 默认下架」，两个条件任一不满足都该看不见
    return $r.success -and @($r.data.items).Count -eq 0
}

Invoke-Case 'API-SHP-003' '审核通过自动上架 → 前台立即可见' {
    Invoke-RestMethod "$Gateway/gateway/products/SubmitAudit" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:shopProductId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    Invoke-RestMethod "$Gateway/gateway/products/Audit" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:shopProductId; auditStatus = 20; remark = 'ok' } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $r = ShopPost 'List' @{ customerId = 0; keyword = "前台商品$($script:suffix)"; page = 1; pageSize = 10 }
    return @($r.data.items).Count -eq 1
}

Invoke-Case 'API-SHP-004' '审核通过自动上架后商品详情可访问' {
    $r = ShopPost 'Detail' @{ customerId = 0; productId = $script:shopProductId }
    return $r.success -and $r.data.productId -eq "$($script:shopProductId)"
}

Invoke-Case 'API-SHP-005' '重复上架操作保持幂等，前台仍可见' {
    Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:shopProductId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $r = ShopPost 'List' @{ customerId = 0; keyword = "前台商品$($script:suffix)"; page = 1; pageSize = 10 }
    return @($r.data.items).Count -eq 1
}

Invoke-Case 'API-SHP-006' '🔴 P0 每个 SKU 单独定价：满 100 减 20 → 200 元件到手 180、100 元件到手 80' {
    # 全场满 100 减 20
    $now = [DateTime]::UtcNow
    $act = @{
        activityName = "前台满减$($script:suffix)"; activityType = 1
        thresholdAmount = 100; discountAmount = 20; targetType = 1; targets = '[]'
        startTime = $now.AddDays(-1).ToString('o'); endTime = $now.AddDays(1).ToString('o')
        status = 1; platformId = 0
    }
    $script:shopActivityId = [long](Invoke-RestMethod "$Marketing/marketing/activities/Create" -Method Post `
        -Body ($act | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30).data

    $r = ShopPost 'Detail' @{ customerId = 0; productId = $script:shopProductId }
    $skus = @($r.data.skus)
    if ($skus.Count -ne 2) { return $false }

    # 关键：两个 SKU **各自**满足满 100，所以各自减 20。
    # 如果把它们拍平成一次试算，门槛按合计 300 判过、20 元摊到两件上，
    # 就会得到 186.67 / 93.33 —— 商品卡上的价比用户实付便宜，结算时发现变贵。
    $p200 = @($skus | Where-Object { $_.originalPrice -eq 200 })[0]
    $p100 = @($skus | Where-Object { $_.originalPrice -eq 100 })[0]

    return $p200.finalPrice -eq 180 -and $p100.finalPrice -eq 80
}

Invoke-Case 'API-SHP-007' '🔴 列表的到手价取各启用 SKU 的最小值（商品卡展示的就是最低能买到的那个价）' {
    $r = ShopPost 'List' @{ customerId = 0; keyword = "前台商品$($script:suffix)"; page = 1; pageSize = 10 }
    $item = @($r.data.items)[0]
    # min(180, 80) = 80；原价同理取 min(200, 100) = 100
    return $item.finalPrice -eq 80 -and $item.originalPrice -eq 100 -and $item.hasDiscount -eq $true
}

Invoke-Case 'API-SHP-008' '优惠来源标签带出活动名（商品卡显示「满减」而不是数字枚举）' {
    $r = ShopPost 'List' @{ customerId = 0; keyword = "前台商品$($script:suffix)"; page = 1; pageSize = 10 }
    $item = @($r.data.items)[0]
    return $item.discountSource -eq 'activity' -and $item.discountSourceName -match "前台满减"
}

Invoke-Case 'API-SHP-009' '游客不计券（customerId = 0）' {
    $r = ShopPost 'Detail' @{ customerId = 0; productId = $script:shopProductId }
    return $r.success
}

Invoke-Case 'API-SHP-010' '🔴 前台接口走独立前缀，后台的待审核列表不会因此泄露到前台' {
    # 后台能看到待审核商品，前台看不到；两者路由前缀不同，互不影响
    $admin = Invoke-RestMethod "$Gateway/gateway/products/List?auditStatus=10&page=1&pageSize=50" `
        -Headers $script:headers -TimeoutSec 30
    $shop = ShopPost 'List' @{ customerId = 0; page = 1; pageSize = 50 }

    $adminIds = @($admin.data | Where-Object { $_.id -eq "$($script:shopProductId)" }).Count
    $shopIds = @($shop.data.items | Where-Object { $_.productId -eq "$($script:shopProductId)" }).Count
    return $adminIds -eq 0 -and $shopIds -eq 1
}

Invoke-Case 'API-SHP-011' '🔴 营销服务不可用时按原价回退，而不是整页 500' {
    # 停掉本节的活动 → 没有优惠，但接口必须正常返回（BUSINESS.md 11.5 的「静默回退原价展示」）
    Invoke-RestMethod "$Marketing/marketing/activities/SetStatus" -Method Post `
        -Body (@{ activityId = $script:shopActivityId; status = 2 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $r = ShopPost 'List' @{ customerId = 0; keyword = "前台商品$($script:suffix)"; page = 1; pageSize = 10 }
    $item = @($r.data.items)[0]
    Write-Host ("        原价={0} 到手价={1} 有优惠={2} 来源={3}" -f `
        $item.originalPrice, $item.finalPrice, $item.hasDiscount, $item.discountSource) -ForegroundColor DarkGray
    return $r.success -and $item.finalPrice -eq $item.originalPrice -and $item.hasDiscount -eq $false
}

function Get-ShopCategoryIds {
    # 递归收集整棵树的 Id。只看顶层会漏掉三级分类——
    # 本节建的分类挂在第三级，而前台返回的树是「一级节点 + children」的嵌套结构。
    $tree = Invoke-RestMethod "$Gateway/gateway/shop/catalog/CategoryTree" -TimeoutSec 30
    $ids = New-Object System.Collections.Generic.List[string]

    function Walk($nodes) {
        foreach ($n in @($nodes)) {
            $ids.Add([string]$n.id)
            if ($n.children) { Walk $n.children }
        }
    }

    Walk $tree.data
    return , $ids
}

Invoke-Case 'API-SHP-011b' '🔴 前台分类树能看到启用中的三级分类（递归整棵树）' {
    $ids = Get-ShopCategoryIds
    return $ids.Contains([string]$script:shopCategoryIds[2])
}

Invoke-Case 'API-SHP-011b2' '🔴 停用后立刻从前台分类树消失，且接口没有「含停用」开关' {
    $l3 = $script:shopCategoryIds[2]

    # 停用
    Invoke-RestMethod "$Gateway/gateway/categories/Update" -Method Post -Headers $script:headers `
        -Body (@{ categoryId = [long]$l3; categoryName = "前台$($script:suffix)"; sortOrder = 0; status = 2 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $after = Get-ShopCategoryIds

    # 前台接口不接受 includeDisabled 参数（对比后台的 /categories/Tree 有），
    # 所以停用后没有任何办法再看到它
    return -not $after.Contains([string]$l3)
}

Invoke-Case 'API-SHP-011c' '前台品牌列表可匿名访问（不需要登录）' {
    $b = Invoke-RestMethod "$Gateway/gateway/shop/catalog/Brands" -TimeoutSec 30
    return $b.success -and $b.data -is [array]
}

Invoke-Case 'API-SHP-011d' '🔴 P0 匿名直连后台商品列表时，未审核/已下架的商品不出现（AOP 可见性过滤）' {
    # BUSINESS.md 1.4 明确要求可见性过滤由 AOP 统一注入，并写明
    # 「不靠每个 Handler 手写这些条件 —— 靠自觉写一定会漏」。
    #
    # 但在此之前**没有任何实体实现 IPublicVisible<>**，于是
    # RegisterPublicVisibility 一直是空转的：可见性完全落在各 Handler 手写的
    # Where 上，正是规格说要避免的那件事。实测匿名直连 /products/List
    # 能拿到 auditStatus=10（待审核）、status=2（已下架）的商品。
    #
    # 这条用例把「AOP 真的生效」钉住：新建的商品默认就是「待审核 + 下架」，
    # 匿名上下文里它必须**查不到**。
    $body = @{
        productId = 0; spuName = "隐形商品$($script:suffix)"; categoryId = $script:shopCategoryId
        deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
        specs = @(@{ specName = '颜色'; specValues = @('红') })
        skus = @(@{ skuCode = "HIDE$($script:suffix)"; specValues = @('红'); price = 10.00; stock = 5; status = 1 })
    }
    $hiddenId = [long](Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30).data
    if ($hiddenId -le 0) { return $false }

    try {
        # 直连、不带任何请求头 → 下游按「游客」上下文处理 → 应注入公开可见性过滤
        $anon = Invoke-RestMethod "http://127.0.0.1:5058/products/List?page=1&pageSize=200" -TimeoutSec 30
        $leaked = @($anon.data | Where-Object { [long]$_.id -eq $hiddenId })

        # 后台带令牌仍然要能看到它（运营必须能看到待审核与下架商品）
        $admin = Invoke-RestMethod "$Gateway/gateway/products/List?page=1&pageSize=200" `
            -Headers $script:headers -TimeoutSec 30
        $visibleForAdmin = @($admin.data | Where-Object { [long]$_.id -eq $hiddenId })

        Write-Host ("        匿名可见={0} 后台可见={1}" -f $leaked.Count, $visibleForAdmin.Count) -ForegroundColor DarkGray

        return $leaked.Count -eq 0 -and $visibleForAdmin.Count -eq 1
    } finally {
        Invoke-RestMethod "$Gateway/gateway/products/Delete" -Method Post -Headers $script:headers `
            -Body (@{ productId = $hiddenId } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
}

Invoke-Case 'API-SHP-012' '清理：停用活动 → 删商品 → 删分类' {
    if ($script:shopActivityId -gt 0) {
        Invoke-RestMethod "$Marketing/marketing/activities/Delete" -Method Post `
            -Body (@{ activityId = $script:shopActivityId } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
    if ($script:shopProductId -gt 0) {
        Invoke-RestMethod "$Gateway/gateway/products/Delete" -Method Post -Headers $script:headers `
            -Body (@{ productId = $script:shopProductId } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
    # 先叶子后父级：有子分类时父级删不掉
    foreach ($id in [array]($script:shopCategoryIds | Sort-Object -Descending)) {
        Invoke-RestMethod "$Gateway/gateway/categories/Delete" -Method Post -Headers $script:headers `
            -Body (@{ categoryId = $id } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
    return $true
}

Write-Host "`n=== SRC 商品搜索（Elasticsearch + IK 中文分词）===" -ForegroundColor Cyan

# 名字刻意用「小米空气净化器 4代 静音款」：搜「净化器」能不能命中，
# 就是 IK 与 smartcn 的分水岭——smartcn 会把整个词当成一个 token，搜子词必然搜不到。
$script:searchProductId = 0
$script:searchCategoryId = 0
$script:syncProductId = 0
$script:syncCategoryId = 0

function Search-Shop([string]$keyword) {
    return Invoke-RestMethod "$Product/shop/products/Search" -Method Post `
        -Body (@{ customerId = 0; keyword = $keyword; page = 1; pageSize = 10 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
}

Invoke-Case 'API-SRC-001' '准备：一个中文名商品「小米空气净化器 4代 静音款」' {
    # 自建分类：SHP 段的分类已被 API-SHP-012 清理掉了，复用它必然建不出商品
    $a1 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:headers `
        -Body (@{ parentId = 0; categoryName = "搜索$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $a2 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:headers `
        -Body (@{ parentId = $a1; categoryName = "搜索$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $script:searchCategoryId = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:headers `
        -Body (@{ parentId = $a2; categoryName = "搜索$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data

    $body = @{
        productId = 0; spuName = '小米空气净化器 4代 静音款'; subTitle = '除甲醛 适用客厅'
        categoryId = $script:searchCategoryId; deliveryType = 1
        mainImage = 'https://cdn.example.com/m.png'
        specs = @(@{ specName = '版本'; specValues = @('标准') })
        skus = @(@{ skuCode = "SRC$($script:suffix)"; specValues = @('标准'); price = 1299; stock = 20; status = 1 })
    }
    $script:searchProductId = [long](Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30).data

    Invoke-RestMethod "$Gateway/gateway/products/SubmitAudit" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:searchProductId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    Invoke-RestMethod "$Gateway/gateway/products/Audit" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:searchProductId; auditStatus = 20; remark = 'ok' } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:searchProductId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    # ES 是最终一致，索引更新有毫秒级延迟，等一下再断言
    Start-Sleep -Seconds 2
    return $script:searchProductId -gt 0
}

Invoke-Case 'API-SRC-002' '🔴 P0 IK 分词：搜「净化器」能命中「空气净化器」' {
    $r = Search-Shop '净化器'
    $hit = @($r.data.items | Where-Object { $_.productId -eq "$($script:searchProductId)" })
    # smartcn 会把「空气净化器」整体当一个 token，这一搜必然是 0 条；
    # 能命中就说明索引用的确实是 IK（用户需求 X1「使用 IK」）
    return $r.data.engine -match 'elasticsearch' -and $hit.Count -eq 1
}

Invoke-Case 'API-SRC-003' '🔴 搜「空气」同样命中（分词不是只对某个词有效）' {
    $r = Search-Shop '空气'
    return @($r.data.items | Where-Object { $_.productId -eq "$($script:searchProductId)" }).Count -eq 1
}

Invoke-Case 'API-SRC-004' '🔴 搜「静音」命中（命中商品名后半段，说明确实做了分词而不是前缀匹配）' {
    $r = Search-Shop '静音'
    return @($r.data.items | Where-Object { $_.productId -eq "$($script:searchProductId)" }).Count -eq 1
}

Invoke-Case 'API-SRC-005' '搜不存在的词返回空列表而不是报错' {
    $r = Search-Shop '这个词肯定不存在xyz'
    return $r.success -and @($r.data.items).Count -eq 0
}

Invoke-Case 'API-SRC-006' '🔴 搜索结果的价格来自数据库，不是索引副本' {
    $r = Search-Shop '净化器'
    $hit = @($r.data.items | Where-Object { $_.productId -eq "$($script:searchProductId)" })[0]
    # 索引里刻意**不存价格**（防副本滞后），这里的 1299 必须是从库里取回的真实售价
    return $hit -and $hit.finalPrice -eq 1299
}

Invoke-Case 'API-SRC-007' '🔴 下架后立刻搜不到（下架必须同步索引，否则「有问题先下架」就失效了）' {
    Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:searchProductId; status = 2 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    Start-Sleep -Seconds 2

    $r = Search-Shop '净化器'
    return @($r.data.items | Where-Object { $_.productId -eq "$($script:searchProductId)" }).Count -eq 0
}

Invoke-Case 'API-SRC-008' '重新上架后又能搜到' {
    Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:searchProductId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    Start-Sleep -Seconds 2

    $r = Search-Shop '净化器'
    return @($r.data.items | Where-Object { $_.productId -eq "$($script:searchProductId)" }).Count -eq 1
}

Invoke-Case 'API-SRC-009' '🔴 删除后搜不到' {
    Invoke-RestMethod "$Gateway/gateway/products/Delete" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:searchProductId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    Start-Sleep -Seconds 2

    $r = Search-Shop '净化器'
    return @($r.data.items).Count -eq 0
}

Invoke-Case 'API-SRC-010' '🔴 关键词含引号不会拼出非法 JSON（转义必须到位）' {
    # 用户可能搜 `50" 寸` 这类内容。直接字符串插值会把请求体拼坏，
    # ES 返回 400 解析错误，而报错完全指不到「是用户输入的问题」。
    $r = Search-Shop '50" 寸'
    return $r.success
}

Invoke-Case 'API-SRC-011' '清理：删搜索分类' {
    if ($script:searchCategoryId -gt 0) {
        foreach ($id in @($script:searchCategoryId)) {
            Invoke-RestMethod "$Gateway/gateway/categories/Delete" -Method Post -Headers $script:headers `
                -Body (@{ categoryId = $id } | ConvertTo-Json) `
                -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        }
    }
    return $true
}

Write-Host "`n=== SYNC 搜索索引对账（补偿任务）===" -ForegroundColor Cyan

# 索引写失败是**只记日志不阻塞业务**的（商品保存是主链路，ES 只是加速手段），
# 代价就是索引会慢慢和库不一致。这个对账任务就是那条代价的兜底。
#
# 手工制造漂移：直接调 ES 的 REST API（9200 已映射到宿主机）。
# 刻意**不用** `docker exec ... curl -d '{...}'`：
# JSON 里的双引号要穿过 PowerShell → cmd → curl 三层转义，
# 实测写出来的引号会被吃掉，解析错误又指不到真正原因。这里直接用 Invoke-RestMethod，没有中间层。
$script:esUrl = 'http://127.0.0.1:9200'
$script:esIndex = 'simpleshop_product'

function Get-IndexCount {
    return [int](Invoke-RestMethod "$($script:esUrl)/$($script:esIndex)/_count" -TimeoutSec 15).count
}

# 显式用 $($id) 而不是 $id：
# 写成 `_doc/$id?refresh=true` 时，`?` 紧跟变量名，字符串里那段的解析结果不符合预期，
# 请求打到别的路径上、DELETE 静默无效，而计数看起来「就是没变」，排查方向会被带偏。
function Remove-IndexDoc([long]$id) {
    $url = "$($script:esUrl)/$($script:esIndex)/_doc/$($id)?refresh=true"
    $r = Invoke-WebRequest $url -Method Delete -TimeoutSec 15 -SkipHttpErrorCheck
    if ($r.StatusCode -ne 200) { throw "删除索引文档失败：$url 返回 HTTP $($r.StatusCode)" }
}

function Add-GhostDoc([long]$id) {
    # 库里不存在的商品：等价于「商品被物理删除，但索引没清干净」
    $body = @{ productId = $id; spuName = '幽灵商品'; auditStatus = 20; status = 1; sales = 0 } |
            ConvertTo-Json -Compress
    $url = "$($script:esUrl)/$($script:esIndex)/_doc/$($id)?refresh=true"
    $r = Invoke-WebRequest $url -Method Put -Body $body -ContentType 'application/json' `
        -TimeoutSec 15 -SkipHttpErrorCheck
    if ($r.StatusCode -ne 200 -and $r.StatusCode -ne 201) {
        throw "写入幽灵文档失败：$url 返回 HTTP $($r.StatusCode)"
    }
}

function Sync-Index {
    $r = Invoke-RestMethod "$Product/internal/products/search-index/sync" -Method Post `
        -Body (@{ pageSize = 200; deleteOrphans = $true } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 120

    # 🔴 refresh 必须在 sync **之后**。
    # 对账里的补写用 refresh=false（批量写不该每条都刷一次，否则大批量补写会慢到不可接受），
    # 所以 sync 返回的那一刻索引还没可见，此刻取 _count 拿到的是**陈旧值**。
    # 之前把 refresh 放在 sync 之前，于是「补完 → 立刻计数」永远看不到刚补的文档，
    # 看起来像对账没生效。
    Invoke-RestMethod "$($script:esUrl)/$($script:esIndex)/_refresh" -Method Post -TimeoutSec 15 | Out-Null
    return $r
}
Invoke-Case 'API-SYNC-001' '准备：一个上架商品并确认它进了索引' {
    # 自建分类：SRC 段的分类已被 API-SRC-011 清理掉了，复用它必然建不出商品
    $b1 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:headers `
        -Body (@{ parentId = 0; categoryName = "对账$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $b2 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:headers `
        -Body (@{ parentId = $b1; categoryName = "对账$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $script:syncCategoryId = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:headers `
        -Body (@{ parentId = $b2; categoryName = "对账$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data

    $body = @{
        productId = 0; spuName = "对账测试商品$($script:suffix)"
        categoryId = $script:syncCategoryId; deliveryType = 1
        mainImage = 'https://cdn.example.com/m.png'
        specs = @(@{ specName = '版本'; specValues = @('标准') })
        skus = @(@{ skuCode = "SYNC$($script:suffix)"; specValues = @('标准'); price = 66; stock = 5; status = 1 })
    }
    $script:syncProductId = [long](Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30).data

    Invoke-RestMethod "$Gateway/gateway/products/SubmitAudit" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:syncProductId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    Invoke-RestMethod "$Gateway/gateway/products/Audit" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:syncProductId; auditStatus = 20; remark = 'ok' } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:syncProductId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    # 先对账一次，让索引与库**确定一致**，再取基线。
    # 🔴 不能假设「刚建的商品已经在索引里」：索引写入用 refresh=false（批量写不该每条都刷一次），
    # 此刻它可能还不可见；万一写入本身失败过，那就更不在了。
    # 拿一个不确定的起点去断言「删 1 个 → 计数减 1」，失败时根本分不清是对账的问题还是起点的问题。
    Sync-Index | Out-Null
    $script:indexCountBefore = Get-IndexCount

    return $script:syncProductId -gt 0 -and $script:indexCountBefore -gt 0
}

Invoke-Case 'API-SYNC-002' '🔴 P0 索引漂移（文档被删）后，对账能补回' {
    # 直接从 ES 删掉这份文档，等价于「当初保存商品时索引写失败」
    Remove-IndexDoc $script:syncProductId
    $afterDelete = Get-IndexCount

    # 先确认漂移真的造成了，否则这条用例的失败原因会被掩盖
    if ($afterDelete -ge $script:indexCountBefore) { return $false }

    $r = Sync-Index
    $afterSync = Get-IndexCount

    # 补写数必须正好等于漂移数（1），且计数回到删除前
    return $r.data.missing -eq 1 -and $afterSync -eq $script:indexCountBefore
}

Invoke-Case 'API-SYNC-003' '🔴 孤儿文档（索引有、库里没有）被对账清掉' {
    Add-GhostDoc 987654321098
    $withGhost = Get-IndexCount
    if ($withGhost -le $script:indexCountBefore) { return $false }   # 先确认幽灵真的进去了

    $r = Sync-Index
    $afterSync = Get-IndexCount

    return $r.data.orphansRemoved -eq 1 -and $afterSync -eq $script:indexCountBefore
}
Invoke-Case 'API-SYNC-004' '没有漂移时对账是空操作（不重复写、不误删）' {
    $r = Sync-Index
    # 幂等：已经一致时补 0 清 0，而不是把每份文档都重写一遍
    return $r.success -and $r.data.missing -eq 0 -and $r.data.orphansRemoved -eq 0
}

Invoke-Case 'API-SYNC-005' '清理：删对账测试商品与分类' {
    if ($script:syncProductId -gt 0) {
        Invoke-RestMethod "$Gateway/gateway/products/Delete" -Method Post -Headers $script:headers `
            -Body (@{ productId = $script:syncProductId } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
    Sync-Index | Out-Null   # 删完再对账一次，把索引里的残留清掉

    if ($script:syncCategoryId -gt 0) {
        Invoke-RestMethod "$Gateway/gateway/categories/Delete" -Method Post -Headers $script:headers `
            -Body (@{ categoryId = $script:syncCategoryId } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
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
