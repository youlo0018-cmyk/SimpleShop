<#
.SYNOPSIS
    向 AgileConfig 写入各服务的配置并发布。
.DESCRIPTION
    走 AgileConfig 自己的 REST API（而不是手写 SQL 插 agc_* 表），
    这样发布时间线、已发布快照、MD5 等不变量由 AgileConfig 自己维护。
    幂等：已存在的配置项会被更新，不会重复插入。
    依据：DATA_SPEC.md 1.4（每服务独立配置）、PLAN.md S0。
#>
[CmdletBinding()]
param(
    [string]$Server = 'http://127.0.0.1:5000',
    [string]$Env = 'DEV',
    [string]$AdminUser = '',
    [string]$AdminPassword = '',
    [string]$AppSecret = '',
    [string[]]$Service
)

$ErrorActionPreference = 'Stop'

# 凭据不在脚本里留默认值，改为从环境变量或 deploy/.env 读取。
# 理由：脚本会进 git，写死配置中心管理密码等于把后门入库。deploy/.env 已在 .gitignore 中。
$envFile = Join-Path $PSScriptRoot '..\deploy\.env'
if (Test-Path $envFile) {
    Get-Content -LiteralPath $envFile -Encoding UTF8 | ForEach-Object {
        if ($_ -match '^\s*([^#=]+?)\s*=\s*(.*)$') {
            $k = $Matches[1].Trim()
            $v = $Matches[2].Trim().Trim('"').Trim("'")
            if (-not [Environment]::GetEnvironmentVariable($k)) {
                [Environment]::SetEnvironmentVariable($k, $v)
            }
        }
    }
}

if (-not $AdminUser) { $AdminUser = $env:AGILECONFIG_ADMIN_USER }
if (-not $AdminPassword) { $AdminPassword = $env:AGILECONFIG_ADMIN_PASSWORD }
if (-not $AppSecret) { $AppSecret = $env:AGILECONFIG_APP_SECRET }

if (-not $AdminUser -or -not $AdminPassword -or -not $AppSecret) {
    throw '缺少凭据。请复制 deploy/.env.example 为 deploy/.env 填入真实值（该文件不入库），或设置环境变量 AGILECONFIG_ADMIN_USER / AGILECONFIG_ADMIN_PASSWORD / AGILECONFIG_APP_SECRET，也可用 -AdminUser / -AdminPassword / -AppSecret 参数传入。'
}

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# ---------- 每个服务的配置项（键用 .NET 配置节语法） ----------
$dbPassword = 'simpleshop_dev_2026'

# 服务名到库名不是机械转换，必须显式映射：
#   CustomerService -> simpleshopcustomer（不是 simpleshopcustomerservice）
#   MerchantPlatformService -> simpleshopmerchant
#   EvaluateService -> simpleshopevaluate
# 依据 BUSINESS.md 3.3 的服务与数据库对照表。
$dbMap = @{
    'AuthService'             = 'simpleshopauth'
    'UserService'             = 'simpleshopuser'
    'CustomerService'         = 'simpleshopcustomer'
    'ToolService'             = 'simpleshoptool'
    'PermissionService'       = 'simpleshoppermission'
    'ProductService'          = 'simpleshopproduct'
    'CartService'             = 'simpleshopcart'
    'InventoryService'        = 'simpleshopinventory'
    'OrderService'            = 'simpleshoporder'
    'PaymentService'          = 'simpleshoppayment'
    'MarketingService'        = 'simpleshopmarketing'
    'MerchantPlatformService' = 'simpleshopmerchant'
    'PointService'            = 'simpleshoppoint'
    'EvaluateService'         = 'simpleshopevaluate'
}

function Get-ServiceConfigs([string]$name, [int]$redisDb) {
    $db = $dbMap[$name]
    if (-not $db) { throw "未在 dbMap 中登记服务 $name，请先补充（BUSINESS.md 3.3）。" }

    $cfg = [ordered]@{
        'ConnectionStrings:Default' = "Host=127.0.0.1;Port=5432;Database=$db;Username=simpleshop_app;Password=$dbPassword;Pooling=true;Maximum Pool Size=20"
        'Redis:ConnectionString'    = '127.0.0.1:6379'
        'Redis:Database'            = "$redisDb"
        'Consul:Address'            = 'http://127.0.0.1:8500'
        'Consul:ServiceName'        = "$name"
        'RabbitMq:Host'             = 'localhost'
        'RabbitMq:Port'             = '5672'
        'RabbitMq:UserName'         = 'simpleshop'
        'RabbitMq:Password'         = $dbPassword
        'RabbitMq:VirtualHost'      = '/'
        'Snowflake:WorkerIdKeyPrefix' = 'snowflake:worker'
        'Snowflake:WorkerIdUpperBound' = '64'
    }
    if ($name -eq 'CustomerService') {
        $cfg['Jwt:Issuer']       = 'simpleshop'
        $cfg['Jwt:Audience']     = 'simpleshop-customer'
        $cfg['Jwt:Secret']       = 'simpleshop_dev_jwt_secret_change_me_in_production_0123456789'
        $cfg['Jwt:ExpireHours']  = '12'
    }
    return $cfg
}

$serviceMap = [ordered]@{ 'CustomerService' = 3 }
if ($Service) { $serviceMap = [ordered]@{}; foreach ($s in $Service) { $serviceMap[$s] = 3 } }

# ---------- 认证：api 系列接口全部走 Basic（管理员账号 + 密码），不需要 JWT ----------
$basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${AdminUser}:${AdminPassword}"))
$basicHeader = @{ Authorization = "Basic $basic" }

foreach ($name in $serviceMap.Keys) {
    Write-Step "配置应用 $name"

    # 1) 应用不存在就创建
    # GET /api/app 返回的是**裸数组**，不是 { data: [...] }；
    # 这里两种形状都兼容，避免服务端调整响应结构时脚本静默失效。
    $existing = Invoke-RestMethod "$Server/api/app?env=$Env" -Headers $basicHeader -TimeoutSec 30
    $appList = if ($existing -is [array]) { $existing } else { @($existing.data) }
    $app = $appList | Where-Object { $_.id -eq $name } | Select-Object -First 1

    if (-not $app) {
        $appBody = @{
            id = $name; name = $name; group = 'default'; secret = $AppSecret
            enabled = $true; inheritanced = $false; inheritancedApps = @()
        } | ConvertTo-Json
        $created = Invoke-RestMethod "$Server/api/app" -Method Post -Headers $basicHeader -Body $appBody -ContentType 'application/json' -TimeoutSec 30
        Write-Host "    已创建应用 $name（secret: $AppSecret）"
    } else {
        Write-Host "    应用已存在，跳过创建"
    }

    # 2) 写入配置项（存在则更新）
    $cfg = Get-ServiceConfigs $name $serviceMap[$name]
    $current = Invoke-RestMethod "$Server/api/config?appId=$name&env=$Env" -Headers $basicHeader -TimeoutSec 30

    foreach ($key in $cfg.Keys) {
        $value = [string]$cfg[$key]
        $group = ($key -split ':')[0]
        $existingItem = $current | Where-Object { $_.key -eq $key } | Select-Object -First 1

        if ($existingItem) {
            $item = @{
                id = $existingItem.id; appId = $name; group = $group
                key = $key; value = $value; description = $key
            } | ConvertTo-Json
            Invoke-RestMethod "$Server/api/config/$($existingItem.id)?env=$Env" -Method Put -Headers $basicHeader -Body $item -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        } else {
            $item = @{
                appId = $name; group = $group; key = $key; value = $value; description = $key
            } | ConvertTo-Json
            Invoke-RestMethod "$Server/api/config?env=$Env" -Method Post -Headers $basicHeader -Body $item -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        }
    }
    Write-Host "    已写入 $($cfg.Count) 个配置项"

    # 3) 发布（客户端只读已发布配置）
    Invoke-RestMethod "$Server/api/app/publish?appId=$name&env=$Env" -Method Post -Headers $basicHeader -TimeoutSec 30 | Out-Null
    Write-Host "    已发布" -ForegroundColor Green
}

Write-Step '完成'

