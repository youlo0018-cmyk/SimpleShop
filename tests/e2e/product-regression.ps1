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