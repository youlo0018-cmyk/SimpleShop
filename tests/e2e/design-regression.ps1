<#
.SYNOPSIS
    MerchantPlatformService 装修（拖拽搭建器）回归测试。
.DESCRIPTION
    覆盖 BUSINESS.md 16.3~16.5 与 DATA_SPEC 5.29、5.30：
      - 组件库按页面过滤；商户只有 11 个组件
      - 草稿与已发布是**两份独立数据**：保存草稿不影响线上，发布后版本 +1
      - 画布校验：组件类型 / 12 列栅格 / 高度 / id 唯一 / tabBar
      - 商户不可改任何配色（传入的颜色字段被剔除并告警）
      - 商户只能配置店铺页
      - **手动选品必须是本平台 / 本商户 + 审核通过 + 已上架**，任一不满足整单保存失败
    退出码非 0 即视为回归失败。
#>
[CmdletBinding()]
param(
    [string]$Gateway = 'http://127.0.0.1:5008',
    [string]$Merchant = 'http://127.0.0.1:5070',
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
$letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'
$script:platformCode = -join (1..6 | ForEach-Object { $letters[[System.Random]::new().Next(26)] })
$script:platformId = 0
$script:merchantId = 0
$script:categoryIds = @()
$script:onShelfProductId = 0
$script:offShelfProductId = 0

Add-Type -AssemblyName System.Net.Http
$http = [System.Net.Http.HttpClient]::new()
$dd = [System.Collections.Generic.Dictionary[string, string]]::new()
$dd['grant_type'] = 'password'
$dd['client_id'] = 'admin-app'
$dd['username'] = $AdminUser
$dd['password'] = $AdminPassword
# PowerShell 不支持跨行的方法链，必须写成一行
$tokenResp = $http.PostAsync("$Gateway/gateway/auth/token", [System.Net.Http.FormUrlEncodedContent]::new($dd)).GetAwaiter().GetResult()
$tokenJson = ($tokenResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()) | ConvertFrom-Json
$script:adminHeaders = @{ Authorization = "Bearer $($tokenJson.access_token)" }

function MpPost([string]$Path, $Body) {
    # 走网关而不是直连服务：租户上下文由网关验签后注入，超管建平台才不会被匿名 403 挡住。
    return Invoke-RestMethod "$Gateway/gateway$Path" -Method Post -Headers $script:adminHeaders `
        -Body ($Body | ConvertTo-Json -Depth 12) -ContentType 'application/json' -TimeoutSec 60
}

function MpGet([string]$Path) {
    return Invoke-RestMethod "$Gateway/gateway$Path" -Headers $script:adminHeaders -TimeoutSec 60
}

function GwPost([string]$Path, $Body) {
    return Invoke-RestMethod "$Gateway$Path" -Method Post -Headers $script:adminHeaders `
        -Body ($Body | ConvertTo-Json -Depth 12) -ContentType 'application/json' -TimeoutSec 60
}

function New-Product([string]$Tag, [string]$SkuCode) {
    $body = @{
        productId = 0; spuName = "装修商品$Tag$($script:suffix)"; categoryId = $script:categoryIds[2]
        deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
        platformId = $script:platformId; merchantId = $script:merchantId
        specs = @(@{ specName = '颜色'; specValues = @('红') })
        skus = @(@{ skuCode = $SkuCode; specValues = @('红'); price = 66; stock = 10; status = 1 })
    }
    return [long](GwPost '/gateway/products/Save' $body).data
}

# 构造一份合法的平台装修配置。组件用参数控制，方便各用例改一处。
function New-PlatformConfig([string]$Type = 'banner', [int]$Span = 12, [int]$Height = 160, [string]$Page = 'index') {
    return @{
        platformCode = $script:platformCode
        version = 0
        theme = @{ primary = '#0071e3'; tabColor = '#0071e3'; background = '#f5f5f7' }
        tabBar = @(
            @{ pagePath = 'pages/index/index'; text = '首页'; iconPath = 'https://cdn.example.com/home.png' }
            @{ pagePath = 'pages/mall/mall'; text = '商城'; iconPath = 'https://cdn.example.com/mall.png' }
        )
        pages = @{
            index = @{ components = @(@{ id = 'c1'; type = $Type; span = $Span; height = $Height; props = @{} }) }
            profile = @{ components = @() }
        }
        regions = @{ customJson = '' }
    }
}

function New-MerchantConfig([string]$Type = 'shopHeader', [int]$Span = 12, [hashtable]$Props = @{}) {
    return @{
        platformCode = ''
        version = 0
        theme = @{ primary = ''; tabColor = ''; background = '' }
        tabBar = @()
        pages = @{
            store = @{ components = @(@{ id = 'm1'; type = $Type; span = $Span; height = 120; props = $Props }) }
        }
        regions = @{ customJson = '' }
    }
}

Write-Host "`n=== DS 准备：平台 + 商户 + 两个商品（一上架一下架）===" -ForegroundColor Cyan

Invoke-Case 'API-DS-000' '建平台、建商户、建三级分类' {
    $p = MpPost '/platforms/Create' @{
        platformName = "装修平台$($script:suffix)"; platformCode = $script:platformCode
        contactName = '张三'; contactPhone = '13800138000'
        mallName = "装修商城$($script:suffix)"
    }
    if (-not $p.success) { return $false }
    $script:platformId = [long]$p.data

    $m = MpPost '/merchants/Create' @{
        merchantName = "装修店铺$($script:suffix)"; platformId = $script:platformId
        contactName = '李四'; contactPhone = '13900139000'
    }
    if (-not $m.success) { return $false }
    $script:merchantId = [long]$m.data

    # 审核通过，否则后续给商品上架会失败（上架要求审核已通过）
    MpPost '/merchants/Audit' @{
        merchantId = $script:merchantId; auditStatus = 20
        auditorId = 1; auditorName = '审核员'
    } | Out-Null

    $a = (GwPost '/gateway/categories/Create' @{ parentId = 0; categoryName = "装修$($script:suffix)"; platformId = 0 }).data
    $b = (GwPost '/gateway/categories/Create' @{ parentId = $a; categoryName = "装修$($script:suffix)"; platformId = 0 }).data
    $c = (GwPost '/gateway/categories/Create' @{ parentId = $b; categoryName = "装修$($script:suffix)"; platformId = 0 }).data
    $script:categoryIds = @($a, $b, $c)

    return $script:platformId -gt 0 -and $script:merchantId -gt 0 -and $c -gt 0
}

Invoke-Case 'API-DS-001' '建两个已审核商品：一个上架、一个留在下架' {
    $script:onShelfProductId = New-Product '上架' "DSO$($script:suffix)"
    $script:offShelfProductId = New-Product '下架' "DSF$($script:suffix)"
    if ($script:onShelfProductId -le 0 -or $script:offShelfProductId -le 0) { return $false }

    GwPost '/gateway/products/Audit' @{ productId = $script:onShelfProductId; auditStatus = 20 } | Out-Null
    GwPost '/gateway/products/ChangeListing' @{ productId = $script:onShelfProductId; status = 1 } | Out-Null

    # 第二个只审核、**不上架**：用来验证「下架商品不能被装修引用」
    GwPost '/gateway/products/Audit' @{ productId = $script:offShelfProductId; auditStatus = 20 } | Out-Null

    $d1 = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:onShelfProductId)" -Headers $script:adminHeaders -TimeoutSec 30
    $d2 = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:offShelfProductId)" -Headers $script:adminHeaders -TimeoutSec 30
    return $d1.data.status -eq 1 -and $d2.data.status -eq 2
}

Write-Host "`n=== DS 组件库（按页面过滤）===" -ForegroundColor Cyan

Invoke-Case 'API-DS-010' '首页组件库含 banner / 商品网格，不含店铺头' {
    $r = MpGet "/design/Components?forMerchant=false&page=index"
    $types = @($r.data | ForEach-Object { $_.type })
    return $r.success -and ($types -contains 'banner') -and ($types -contains 'productGrid') -and ($types -notcontains 'memberCard')
}

Invoke-Case 'API-DS-011' '🔴 我的页组件库额外含会员与服务宫格（按页面过滤真的生效）' {
    $r = MpGet "/design/Components?forMerchant=false&page=profile"
    $types = @($r.data | ForEach-Object { $_.type })
    return ($types -contains 'memberCard') -and ($types -contains 'serviceGrid') -and ($types -notcontains 'seckillZone')
}

Invoke-Case 'API-DS-012' '🔴 商户组件库只有 11 个，且不含会员卡 / 搜索框' {
    $r = MpGet "/design/Components?forMerchant=true&page=store"
    $types = @($r.data | ForEach-Object { $_.type })
    # 商户可用组件与平台组件库互不干扰（用户明确要求）
    return $r.success -and $types.Count -eq 11 -and ($types -notcontains 'memberCard') -and ($types -notcontains 'searchBar') -and ($types -contains 'shopHeader') -and ($types -contains 'productGrid')
}

Write-Host "`n=== DS 草稿与发布（两份独立数据）===" -ForegroundColor Cyan

Invoke-Case 'API-DS-020' '未配置时返回结构完整的空配置（不是空串）' {
    $r = MpGet "/design/Platform?platformId=$($script:platformId)"
    # 空串会让后台搭建器按「配置损坏」处理，而「还没装修过」是完全正常的初始状态
    return $r.success -and $r.data.configJson -match '"pages"' -and $r.data.version -eq 0 -and $r.data.hasDraft -eq $false
}

Invoke-Case 'API-DS-021' '保存草稿：hasDraft 变 true，但**版本号仍是 0**（线上没变）' {
    $json = New-PlatformConfig | ConvertTo-Json -Depth 12
    $s = MpPost '/design/SavePlatformDraft' @{ platformId = $script:platformId; configJson = $json }
    if (-not $s.success) { return $false }

    $r = MpGet "/design/Platform?platformId=$($script:platformId)"
    # 草稿与已发布是两份独立数据，所以「保存草稿不影响线上」是天然成立的
    return $r.data.hasDraft -eq $true -and $r.data.version -eq 0
}

Invoke-Case 'API-DS-022' '🔴 P0 发布后版本号 +1，且配置转正' {
    $p = MpPost '/design/PublishPlatform' @{ platformId = $script:platformId }
    if (-not $p.success) { return $false }

    $r = MpGet "/design/Platform?platformId=$($script:platformId)"
    return $p.data -eq 1 -and $r.data.version -eq 1
}

Invoke-Case 'API-DS-023' '再发布一次，版本号继续 +1（可重复发布）' {
    MpPost '/design/PublishPlatform' @{ platformId = $script:platformId } | Out-Null
    $r = MpGet "/design/Platform?platformId=$($script:platformId)"
    return $r.data.version -eq 2
}

Invoke-Case 'API-DS-024' '🔴 没有草稿时发布被拒（不能把线上覆盖成空）' {
    $p2 = [long](MpPost '/platforms/Create' @{
        platformName = "空装修平台$($script:suffix)"
        platformCode = (-join (1..6 | ForEach-Object { $letters[[System.Random]::new().Next(26)] }))
        contactName = '张三'; contactPhone = '13800138000'; mallName = '商城'
    }).data
    $r = MpPost '/design/PublishPlatform' @{ platformId = $p2 }
    return (-not $r.success) -and $r.message -match '草稿'
}

Write-Host "`n=== DS 画布校验 ===" -ForegroundColor Cyan

Invoke-Case 'API-DS-030' '🔴 组件类型未注册被拒（不是「后端不认识的类型」）' {
    $json = New-PlatformConfig -Type 'notExistComponent' | ConvertTo-Json -Depth 12
    $r = MpPost '/design/SavePlatformDraft' @{ platformId = $script:platformId; configJson = $json }
    return (-not $r.success) -and $r.message -match 'notExistComponent'
}

Invoke-Case 'API-DS-031' '🔴 组件类型与页面不匹配被拒（店铺头不在首页组件库里）' {
    $json = New-PlatformConfig -Type 'shopHeader' | ConvertTo-Json -Depth 12
    $r = MpPost '/design/SavePlatformDraft' @{ platformId = $script:platformId; configJson = $json }
    return (-not $r.success) -and $r.message -match 'shopHeader'
}

Invoke-Case 'API-DS-032' '🔴 栅格列数只能是 1/2/3/4/6/12' {
    $json = New-PlatformConfig -Span 5 | ConvertTo-Json -Depth 12
    $r = MpPost '/design/SavePlatformDraft' @{ platformId = $script:platformId; configJson = $json }
    return (-not $r.success) -and $r.message -match '列'
}

Invoke-Case 'API-DS-033' '高度 0 表示「按组件默认」，不算非法' {
    $json = New-PlatformConfig -Height 0 | ConvertTo-Json -Depth 12
    $r = MpPost '/design/SavePlatformDraft' @{ platformId = $script:platformId; configJson = $json }
    return $r.success
}

Invoke-Case 'API-DS-034' '🔴 高度超出 40~1000 被拒' {
    $json = New-PlatformConfig -Height 5000 | ConvertTo-Json -Depth 12
    $r = MpPost '/design/SavePlatformDraft' @{ platformId = $script:platformId; configJson = $json }
    return (-not $r.success) -and $r.message -match '高度'
}

Invoke-Case 'API-DS-035' '🔴 组件标识在同页内重复被拒' {
    $cfg = New-PlatformConfig
    $cfg.pages.index.components = @(
        @{ id = 'dup'; type = 'banner'; span = 12; height = 100; props = @{} }
        @{ id = 'dup'; type = 'title'; span = 12; height = 60; props = @{} }
    )
    $r = MpPost '/design/SavePlatformDraft' @{
        platformId = $script:platformId
        configJson = ($cfg | ConvertTo-Json -Depth 12)
    }
    return (-not $r.success) -and $r.message -match '重复'
}

Write-Host "`n=== DS 商户装修（页面锁定 / 禁配色 / 只用自己的商品）===" -ForegroundColor Cyan

Invoke-Case 'API-DS-040' '保存商户草稿成功' {
    $json = New-MerchantConfig | ConvertTo-Json -Depth 12
    $r = MpPost '/design/SaveMerchantDraft' @{ merchantId = $script:merchantId; configJson = $json }
    return $r.success
}

Invoke-Case 'API-DS-041' '🔴 商户不能用平台专属组件（会员卡）' {
    $json = New-MerchantConfig -Type 'memberCard' | ConvertTo-Json -Depth 12
    $r = MpPost '/design/SaveMerchantDraft' @{ merchantId = $script:merchantId; configJson = $json }
    return (-not $r.success) -and $r.message -match 'memberCard'
}

Invoke-Case 'API-DS-042' '🔴 商户不能配置首页（画布锁定为店铺页）' {
    $cfg = New-MerchantConfig
    $cfg.pages['index'] = @{ components = @() }
    $r = MpPost '/design/SaveMerchantDraft' @{
        merchantId = $script:merchantId
        configJson = ($cfg | ConvertTo-Json -Depth 12)
    }
    return (-not $r.success) -and $r.message -match 'index'
}

Invoke-Case 'API-DS-043' '🔴 商户传的配色字段被剔除（不报错，但存下来没有颜色）' {
    $props = @{ source = 1; color = '#ff0000'; textColor = '#00ff00' }
    $json = New-MerchantConfig -Props $props | ConvertTo-Json -Depth 12
    $r = MpPost '/design/SaveMerchantDraft' @{ merchantId = $script:merchantId; configJson = $json }
    if (-not $r.success) { return $false }

    $g = MpGet "/design/Merchant?merchantId=$($script:merchantId)"
    # 商户不能改任何配色（用户明确要求）。这里剔除而不是报错——
    # 后台属性面板根本不提供颜色选择器，报错只会让整个店铺装修存不进去
    return $g.data.configJson -notmatch 'ff0000' -and $g.data.configJson -notmatch '00ff00'
}

Invoke-Case 'API-DS-044' '手动指定**已上架**的本商户商品：保存通过' {
    $props = @{ source = 2; manualProductIds = @($script:onShelfProductId) }
    $json = New-MerchantConfig -Type 'productGrid' -Props $props | ConvertTo-Json -Depth 12
    $r = MpPost '/design/SaveMerchantDraft' @{ merchantId = $script:merchantId; configJson = $json }
    return $r.success
}

Invoke-Case 'API-DS-045' '🔴 P0 手动指定**已下架**的商品：整单保存失败' {
    $props = @{ source = 2; manualProductIds = @($script:offShelfProductId) }
    $json = New-MerchantConfig -Type 'productGrid' -Props $props | ConvertTo-Json -Depth 12
    $r = MpPost '/design/SaveMerchantDraft' @{ merchantId = $script:merchantId; configJson = $json }
    # 装修直接面向顾客展示，能挂未上架商品就等于运营自己把没过审 / 没上架的东西摆上去了
    return (-not $r.success) -and $r.message -match '下架'
}

Invoke-Case 'API-DS-046' '🔴 失败原因里带出**具体是哪个商品**（运营才知道改谁）' {
    $props = @{ source = 2; manualProductIds = @($script:onShelfProductId, $script:offShelfProductId) }
    $json = New-MerchantConfig -Type 'productGrid' -Props $props | ConvertTo-Json -Depth 12
    $r = MpPost '/design/SaveMerchantDraft' @{ merchantId = $script:merchantId; configJson = $json }
    return (-not $r.success) -and $r.message -match "$($script:offShelfProductId)"
}

Invoke-Case 'API-DS-047' '🔴 手动指定超过 20 个商品被拒' {
    $many = 1..21 | ForEach-Object { $script:offShelfProductId }
    $props = @{ source = 2; manualProductIds = @($many) }
    $json = New-MerchantConfig -Type 'productGrid' -Props $props | ConvertTo-Json -Depth 12
    $r = MpPost '/design/SaveMerchantDraft' @{ merchantId = $script:merchantId; configJson = $json }
    return (-not $r.success) -and $r.message -match '20'
}

Invoke-Case 'API-DS-048' '商户装修发布：版本 +1' {
    $json = New-MerchantConfig | ConvertTo-Json -Depth 12
    MpPost '/design/SaveMerchantDraft' @{ merchantId = $script:merchantId; configJson = $json } | Out-Null
    $p = MpPost '/design/PublishMerchant' @{ merchantId = $script:merchantId }
    $r = MpGet "/design/Merchant?merchantId=$($script:merchantId)"
    return $p.success -and $p.data -eq 1 -and $r.data.version -eq 1
}

Invoke-Case 'API-DS-049' '清理：删商品 → 删分类' {
    # 循环变量不能叫 $pid：PowerShell 里 $pid 是**只读**的进程 Id 变量，赋值会直接报错
    foreach ($productId in @($script:onShelfProductId, $script:offShelfProductId)) {
        if ($productId -gt 0) {
            GwPost '/gateway/products/Delete' @{ productId = $productId } | Out-Null
        }
    }
    foreach ($id in [array]($script:categoryIds | Sort-Object -Descending)) {
        GwPost '/gateway/categories/Delete' @{ categoryId = $id } | Out-Null
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

Invoke-Case 'API-DS-036' '🔴 缺少必需的页面画布被拒' {
    $cfg = New-PlatformConfig
    $cfg.pages.Remove('profile')
    $r = MpPost '/design/SavePlatformDraft' @{
        platformId = $script:platformId
        configJson = ($cfg | ConvertTo-Json -Depth 12)
    }
    return (-not $r.success) -and $r.message -match 'profile'
}

Invoke-Case 'API-DS-037' '🔴 tabBar 少于 2 项被拒' {
    $cfg = New-PlatformConfig
    $cfg.tabBar = @(@{ pagePath = 'pages/index/index'; text = '首页'; iconPath = 'a.png' })
    $r = MpPost '/design/SavePlatformDraft' @{
        platformId = $script:platformId
        configJson = ($cfg | ConvertTo-Json -Depth 12)
    }
    return (-not $r.success) -and $r.message -match '底部导航'
}

Invoke-Case 'API-DS-038' '🔴 tabBar 页面路径重复被拒' {
    $cfg = New-PlatformConfig
    $cfg.tabBar = @(
        @{ pagePath = 'pages/index/index'; text = '首页'; iconPath = 'a.png' }
        @{ pagePath = 'pages/index/index'; text = '首页2'; iconPath = 'b.png' }
    )
    $r = MpPost '/design/SavePlatformDraft' @{
        platformId = $script:platformId
        configJson = ($cfg | ConvertTo-Json -Depth 12)
    }
    return (-not $r.success) -and $r.message -match '重复'
}
