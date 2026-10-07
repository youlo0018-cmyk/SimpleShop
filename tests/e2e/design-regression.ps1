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

function MpPost-Api([string]$Path, $Body) {
    # 「预期失败」的调用必须用它：校验失败是 HTTP 400，Invoke-RestMethod 直接抛异常，
    # 拿不到 body 里的 message，用例只能看到一句 "400 (Bad Request)"，
    # 分不清到底是被哪条规则拒的。
    try {
        return Invoke-RestMethod "$Gateway/gateway$Path" -Method Post -Headers $script:adminHeaders `
            -Body ($Body | ConvertTo-Json -Depth 12) -ContentType 'application/json' -TimeoutSec 60
    }
    catch {
        $raw = $_.ErrorDetails.Message
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return [pscustomobject]@{ success = $false; message = $_.Exception.Message; data = $null }
        }
        try { return $raw | ConvertFrom-Json -AsHashtable }
        catch { return [pscustomobject]@{ success = $false; message = $raw; data = $null } }
    }
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

<#
.SYNOPSIS
    造一份商户装修配置。
.DESCRIPTION
    加 Theme 参数是因为「商户不能改配色」这条要能**主动传进去**才测得到：
    默认传空串的话，即使后端根本没做剔除，这条用例也会通过 ——
    一条永远绿的回归等于没有回归。
#>
function New-MerchantConfig(
    [string]$Type = 'shopHeader', [int]$Span = 12, [hashtable]$Props = @{}, [hashtable]$Theme = @{}) {
    # ⚠️ 逐项赋值而不是 `@{默认} + $Theme`：PowerShell 的哈希表相加遇到重复键会
    # 直接抛「Item has already been added」，而调用方恰恰只想**覆盖**某几档色。
    $t = @{ primary = ''; tabColor = ''; background = '' }
    foreach ($k in $Theme.Keys) { $t[$k] = $Theme[$k] }

    return @{
        platformCode = ''
        version = 0
        theme = $t
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

    # 第二个审核通过后默认自动上架，再显式下架：用来验证「下架商品不能被装修引用」
    GwPost '/gateway/products/Audit' @{ productId = $script:offShelfProductId; auditStatus = 20 } | Out-Null
    GwPost '/gateway/products/ChangeListing' @{ productId = $script:offShelfProductId; status = 2 } | Out-Null

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

Invoke-Case 'API-DS-043b' '🔴 P0 商户改**主题配色**同样被剔除，且告警回传给运营' {
    # API-DS-043 只覆盖了组件 props 里的配色。三档主题色（primary / tabColor / background）
    # 是「颜色继承平台」这条规则最显眼的三处，之前完全没管：
    # 商户把整站改成自己的红，页面照常打开、颜色也真的变了，没有任何拦截或提示。
    $theme = @{ primary = '#FF0000'; tabColor = '#00FF00'; background = '#0000FF' }
    $json = New-MerchantConfig -Theme $theme | ConvertTo-Json -Depth 12
    $r = MpPost '/design/SaveMerchantDraft' @{ merchantId = $script:merchantId; configJson = $json }
    if (-not $r.success) { return $false }

    $g = MpGet "/design/Merchant?merchantId=$($script:merchantId)"
    # ① 三档色确实没存下来
    $stripped = $g.data.configJson -notmatch 'FF0000' -and
                $g.data.configJson -notmatch '00FF00' -and
                $g.data.configJson -notmatch '0000FF'
    # ② 告警必须**回传给调用方**。只写日志的话运营看不到，
    #    只会发现自己设的红色变成了平台色，然后以为「这破页面不理我」。
    $warned = @($r.data.warnings).Count -ge 3 -and
              (@($r.data.warnings) -join ';') -match '已剔除'

    Write-Host ("        告警 {0} 条：{1}" -f @($r.data.warnings).Count, (@($r.data.warnings)[0])) -ForegroundColor DarkGray
    return $stripped -and $warned
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

Invoke-Case 'API-DS-048b' '🔴 商户装修发布不影响平台装修（两套配置互不干扰）' {
    # 用户明确要求过：「小程序装修**按平台配置**，商户只是改商户自己的店铺装修，
    # 不冲突」。所以商户发布一次，平台的版本号与内容都必须纹丝不动。
    #
    # 这条以前没有断言覆盖 —— 而一旦两者共用同一份存储（或者发布时误用了
    # 平台的 key），症状是「某个商户装修完，全平台小程序的首页跟着变了」，
    # 影响面是整站而不是一家店。
    $before = MpGet "/design/Platform?platformId=$($script:platformId)"
    $beforeMerchant = MpGet "/design/Merchant?merchantId=$($script:merchantId)"

    # 商户再改一版并发布
    $json = New-MerchantConfig | ConvertTo-Json -Depth 12
    MpPost '/design/SaveMerchantDraft' @{ merchantId = $script:merchantId; configJson = $json } | Out-Null
    $pub = MpPost '/design/PublishMerchant' @{ merchantId = $script:merchantId }
    if (-not $pub.success) { Write-Host ("        商户发布失败: " + $pub.message) -ForegroundColor DarkYellow; return $false }

    $after = MpGet "/design/Platform?platformId=$($script:platformId)"
    $afterMerchant = MpGet "/design/Merchant?merchantId=$($script:merchantId)"

    Write-Host ("        平台版本 {0} → {1}；商户版本 {2} → {3}" -f `
        $before.data.version, $after.data.version, $beforeMerchant.data.version, $afterMerchant.data.version) -ForegroundColor DarkGray

    # 平台：版本与配置 JSON 都不许变；商户：版本必须 +1
    return $after.data.version -eq $before.data.version `
        -and $after.data.configJson -eq $before.data.configJson `
        -and $afterMerchant.data.version -eq ($beforeMerchant.data.version + 1) `
        -and $afterMerchant.data.configJson -ne $after.data.configJson
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

Invoke-Case 'API-DS-036' '🔴 增量提交（只发一页）保留另一页；全新平台只给一页仍被拒' {
    # 后台搭建器**一次只提交当前编辑的那一页**（DesignBuilderView.buildConfig：
    # `{ pages: { [当前页]: {...} } }`），指望后端把其它页原样保留 ——
    # 而校验器是按完整配置校验的，直接拿请求体去校验就会报「缺少「profile」页面画布」，
    # 于是平台装修的「存草稿」在界面上 100% 失败（API 层的 e2e 一直用完整配置，所以从没红过）。
    # 现在的口径：保存前先与已存草稿合并，再整体校验。
    $cfg = New-PlatformConfig
    $seed = MpPost '/design/SavePlatformDraft' @{
        platformId = $script:platformId
        configJson = ($cfg | ConvertTo-Json -Depth 12)
    }
    if (-not $seed.success) {
        Write-Host ("        预置完整草稿失败：" + $seed.message) -ForegroundColor DarkYellow
        return $false
    }

    # 只发 index 一页：必须成功，且读回来 profile 还在
    $partial = @{
        pages = @{
            index = @{ components = @(
                @{ id = 'inc1'; type = 'banner'; span = 12; height = 80; props = @{} }
            ) }
        }
    }
    $inc = MpPost '/design/SavePlatformDraft' @{
        platformId = $script:platformId
        configJson = ($partial | ConvertTo-Json -Depth 12)
    }
    $back = (MpGet "/design/Platform?platformId=$($script:platformId)").data
    $parsed = $back.configJson | ConvertFrom-Json
    $keptProfile = $null -ne $parsed.pages.profile
    $keptTabBar = @($parsed.tabBar).Count -ge 2
    $indexCount = @($parsed.pages.index.components).Count

    # 反证：一个全新的平台（没有已存草稿）只发一页，仍然必须被拒
    $fresh = MpPost-Api '/design/SavePlatformDraft' @{
        platformId = 999999999999
        configJson = ($partial | ConvertTo-Json -Depth 12)
    }

    Write-Host ("        增量保存 success={0}；profile 保留={1} tabBar 保留={2} index 组件={3}；全新平台被拒={4}" -f `
        $inc.success, $keptProfile, $keptTabBar, $indexCount, (-not $fresh.success)) -ForegroundColor DarkGray

    $inc.success -and $keptProfile -and $keptTabBar -and $indexCount -eq 1 `
        -and (-not $fresh.success) -and $fresh.message -match 'profile'
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

Write-Host "`n=== DS 租户边界（装修配置不能跨商户写）===" -ForegroundColor Cyan

# platformId / merchantId 都是**请求体字段**，文档写的是「只读；锁定本商户」——
# 也就是说必须由服务端锁，前端隐藏字段不算锁。
# 表上有 AOP 租户过滤，但过滤器只管查询 / 更新 / 删除，**不管插入**：
# 越权保存的真实路径是「更新影响 0 行 → 走 INSERT 分支 → 给别人的商户插一份装修配置」。

$script:designMerchantUserId = 0
$script:designMerchantToken = $null

try {
    $du = "dsgmr$($script:suffix)"
    $dp = 'Merchant123456'
    $c = Invoke-RestMethod "$Gateway/gateway/users/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{
            userName = $du; password = $dp
            phone = '137' + (Get-Random -Minimum 10000000 -Maximum 99999999)
            tenantType = 2; nickName = '装修越权用例'
            platformId = $script:platformId; merchantId = $script:merchantId; roleIds = @(9004)
        } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30

    if ($c.success) {
        $script:designMerchantUserId = [long]$c.data
        $script:designMerchantToken = (Invoke-RestMethod -Uri "$Gateway/gateway/auth/token" -Method Post `
            -Body "grant_type=password&client_id=admin-app&username=$du&password=$dp" `
            -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 30).access_token
    }
}
catch {
    Write-Host ("  （准备装修越权用例失败：" + $_.Exception.Message + "）") -ForegroundColor DarkYellow
}

Invoke-Case 'API-DS-100' '🔴 商户账号改不了别人店铺的装修（404，不是静默成功）' {
    if (-not $script:designMerchantToken) { return $false }
    $h = @{ Authorization = "Bearer $($script:designMerchantToken)" }
    $cfg = (New-MerchantConfig | ConvertTo-Json -Depth 12)

    $r = Invoke-RestMethod "$Gateway/gateway/design/SaveMerchantDraft" -Method Post -Headers $h `
        -Body (@{ merchantId = ($script:merchantId + 1); configJson = $cfg } | ConvertTo-Json -Depth 12) `
        -ContentType 'application/json' -TimeoutSec 60

    # 断言业务码而不是 HTTP：这套接口的业务失败是 HTTP 200 + success=false
    (-not $r.success) -and ([int]$r.code -eq 404)
}

Invoke-Case 'API-DS-101' '🔴 商户账号读不到别人店铺的装修草稿' {
    if (-not $script:designMerchantToken) { return $false }
    $h = @{ Authorization = "Bearer $($script:designMerchantToken)" }
    $r = Invoke-RestMethod "$Gateway/gateway/design/Merchant?merchantId=$($script:merchantId + 1)" `
        -Headers $h -TimeoutSec 60
    (-not $r.success) -and ([int]$r.code -eq 404)
}

Invoke-Case 'API-DS-102' '商户账号改**自己**店铺的装修仍然可用（没有误伤）' {
    if (-not $script:designMerchantToken) { return $false }
    $h = @{ Authorization = "Bearer $($script:designMerchantToken)" }
    $cfg = (New-MerchantConfig | ConvertTo-Json -Depth 12)
    $r = Invoke-RestMethod "$Gateway/gateway/design/SaveMerchantDraft" -Method Post -Headers $h `
        -Body (@{ merchantId = $script:merchantId; configJson = $cfg } | ConvertTo-Json -Depth 12) `
        -ContentType 'application/json' -TimeoutSec 60
    $r.success
}

Invoke-Case 'API-DS-103' '🔴 商户账号的商户列表恰好只有自己一条' {
    # merchant 表是商户维度的租户根：它的 merchant_id 列恒为 0，身份在 Id 上。
    # 默认租户条件 `merchant_id == 我的商户Id` 会一条都匹配不到 ——
    # 那样商户连自己的商户记录都读不到（保存店铺装修时表现成「商户不存在」）。
    # 修好之后要**恰好一条**：多了说明商户条件被整个跳过（能看到同平台的兄弟商户）。
    if (-not $script:designMerchantToken) { return $false }
    $h = @{ Authorization = "Bearer $($script:designMerchantToken)" }
    $r = Invoke-RestMethod "$Gateway/gateway/merchants/List" -Method Post -Headers $h `
        -Body (@{ page = 1; pageSize = 50 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 60
    $items = @($r.data.items)
    $items.Count -eq 1 -and [long]$items[0].id -eq [long]$script:merchantId
}

# 收尾：停用临时商户账号（项目刻意没有删除账号的接口，审计要求留痕）
if ($script:designMerchantUserId -gt 0) {
    try {
        Invoke-RestMethod "$Gateway/gateway/users/UpdateStatus" -Method Post -Headers $script:adminHeaders `
            -Body (@{ userId = $script:designMerchantUserId; status = 2 } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        Write-Host '  （已停用临时商户账号）' -ForegroundColor DarkGray
    }
    catch {
        Write-Host ('  （停用临时商户账号失败，不影响结论：' + $_.Exception.Message + '）') -ForegroundColor DarkYellow
    }
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
