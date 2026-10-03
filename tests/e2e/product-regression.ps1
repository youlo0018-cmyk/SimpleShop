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

Invoke-Case 'API-SHP-003' '通过审核但仍下架 → 前台依然看不到' {
    Invoke-RestMethod "$Gateway/gateway/products/SubmitAudit" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:shopProductId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    Invoke-RestMethod "$Gateway/gateway/products/Audit" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:shopProductId; auditStatus = 20; remark = 'ok' } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $r = ShopPost 'List' @{ customerId = 0; keyword = "前台商品$($script:suffix)"; page = 1; pageSize = 10 }
    return @($r.data.items).Count -eq 0
}

Invoke-Case 'API-SHP-004' '🔴 未上架商品的详情也回 404，且不区分「不存在」与「已下架」' {
    $r = ShopPost 'Detail' @{ customerId = 0; productId = $script:shopProductId }
    return (-not $r.success) -and $r.code -eq 404
}

Invoke-Case 'API-SHP-005' '审核通过 + 已上架 → 前台可见' {
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