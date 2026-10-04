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

# 客户 JWT 的 HS256 密钥。CustomerService 签发、Gateway 验签，必须是同一个值——
# 提成变量就是为了避免两处各写一份、改了一处忘了另一处。
$customerJwtSecret = 'simpleshop_dev_jwt_secret_change_me_in_production_0123456789'

# 网关与下游服务之间的内部共享口令：下游只在这个口令正确时才采信 X-Claim-* 请求头。
# 必须与 Gateway 用的是同一个值，配置项名叫 Tenancy:InternalToken。
# 没配的后果是「所有人按匿名处理」——超管接口全 403，fail-closed 而不是放行。
if (-not $env:INTERNAL_SERVICE_TOKEN) {
    if (-not $env:AGILECONFIG_INTERNAL_TOKEN) {
        throw '缺少 INTERNAL_SERVICE_TOKEN。请在 deploy/.env 里设置（该文件不入库），网关与各服务要用同一个值。'
    }
    $env:INTERNAL_SERVICE_TOKEN = $env:AGILECONFIG_INTERNAL_TOKEN
}
$internalToken = $env:INTERNAL_SERVICE_TOKEN

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
    # 定时任务目前只调别人接口、不读写自己的库：同样留空。
    # 将来要落「补偿重试记录」表时再改成 simpleshopscheduled。
    'ScheduledService'        = ''
    # 网关不连数据库：留空即可，脚本会跳过写 ConnectionStrings:Default。
    # 硬塞一个连不上的连接串只会误导后来排查的人。
    'Gateway'                 = ''
    # 日志服务的数据全在 Elasticsearch，没有任何业务表：留空。
    'LogService'              = ''
}

function Get-ServiceConfigs([string]$name, [int]$redisDb) {
    $db = $dbMap[$name]
    if ($null -eq $db) { throw "未在 dbMap 中登记服务 $name，请先补充（BUSINESS.md 3.3）。无库的服务写空串。" }

    $cfg = [ordered]@{}

    # 无库的服务（如 Gateway）不写连接串，而不是写一个假的。
    # 填一个永远用不上的连接串会误导后来排查的人。
    if ($db) {
        $cfg['ConnectionStrings:Default'] = "Host=127.0.0.1;Port=5432;Database=$db;Username=simpleshop_app;Password=$dbPassword;Pooling=true;Maximum Pool Size=20"
    }

    # LogService 不连 Redis：它不做锁、不做缓存、也不需要雪花 Id
    #（写 ES 时文档 Id 由 ES 自己生成，日志记录本来就不需要业务 Id）。
    # 所以这两个键也不写，让它的 Program.cs 走显式豁免，
    # 而不是配一个连上了却永远不用的 Redis。
    if ($name -eq 'LogService') {
        $cfg['LogIndex:Url']             = 'http://127.0.0.1:9200'
        $cfg['LogIndex:PvIndex']         = 'simpleshop_log_pv'
        $cfg['LogIndex:OperationIndex']  = 'simpleshop_log_operation'
        $cfg['LogIndex:ExceptionIndex']  = 'simpleshop_log_exception'
        $cfg['LogIndex:DeadLetterIndex'] = 'simpleshop_log_deadletter'

        # 消费队列名必须与其它服务都不同，否则会互相抢消息。
        # MaxAttempts=3：日志丢得起，但也不能无限重试把队列堵住。
        $cfg['EventConsumer:QueueName']        = 'simpleshop.log'
        $cfg['EventConsumer:MaxAttempts']      = '3'
        $cfg['EventConsumer:PrefetchCount']    = '50'
        $cfg['EventConsumer:RetryDelaySeconds'] = '2'

        # 死信重放上限。超过就不再放：靠下游没配好而失败的消息，
        # 重放一百次也是同样地失败，只会把刚恢复的下游再打挂。
        $cfg['DeadLetter:MaxReplayCount'] = '3'
    } else {
        $cfg['Redis:ConnectionString'] = '127.0.0.1:6379'
        $cfg['Redis:Database']         = "$redisDb"
    }
    $cfg['Consul:Address']            = 'http://127.0.0.1:8500'
    $cfg['Consul:ServiceName']        = "$name"
    $cfg['RabbitMq:Host']             = 'localhost'
    $cfg['RabbitMq:Port']             = '5672'
    $cfg['RabbitMq:UserName']         = 'simpleshop'
    $cfg['RabbitMq:Password']         = $dbPassword
    $cfg['RabbitMq:VirtualHost']      = '/'
    # Elasticsearch：商品搜索（BUSINESS.md 15.2 / 用户需求 X1 用 IK 分词）。
    # 索引与分词器都由 ProductService 自己管，所以**每个服务都给一份**：
    # 少给一份会导致别的服务读到「配置不全」而拒绝启动，多给一份只是几行 YAML。
    # 分词器装不上时降级 smartcn —— 见 deploy/elasticsearch/Dockerfile。
    $cfg['Elasticsearch:Url']       = 'http://127.0.0.1:9200'
    $cfg['Elasticsearch:IndexName'] = 'simpleshop_product'
    $cfg['Snowflake:WorkerIdKeyPrefix'] = 'snowflake:worker'
    $cfg['Snowflake:WorkerIdUpperBound'] = '64'
    $cfg['Tenancy:InternalToken']    = $internalToken
    # 下游服务地址。服务之间的 HTTP 调用地址只在这里出现，代码里不写死端口。
    # 端口表见 scripts/service-registry.json（与 BUSINESS.md 3.1 的服务表一致）。
    $serviceUrls = @{
        'UserService'             = 'http://127.0.0.1:5011'
        'CustomerService'         = 'http://127.0.0.1:5280'
        'ToolService'             = 'http://127.0.0.1:5080'
        'PermissionService'       = 'http://127.0.0.1:5022'
        'AuthService'             = 'http://127.0.0.1:5019'
        'ProductService'          = 'http://127.0.0.1:5058'
        'CartService'             = 'http://127.0.0.1:5060'
        'InventoryService'        = 'http://127.0.0.1:5062'
        'OrderService'            = 'http://127.0.0.1:5064'
        'PaymentService'          = 'http://127.0.0.1:5066'
        'MarketingService'        = 'http://127.0.0.1:5072'
        'MerchantPlatformService' = 'http://127.0.0.1:5070'
        'PointService'            = 'http://127.0.0.1:5082'
        'EvaluateService'         = 'http://127.0.0.1:5084'
    }
    $cfg['Services:PermissionServiceBaseUrl'] = $serviceUrls['PermissionService']
    $cfg['Services:UserServiceBaseUrl']        = $serviceUrls['UserService']
    $cfg['Services:AuthServiceBaseUrl']        = $serviceUrls['AuthService']
    $cfg['Services:InventoryServiceBaseUrl']   = $serviceUrls['InventoryService']
    $cfg['Services:ProductServiceBaseUrl']    = $serviceUrls['ProductService']
    $cfg['Services:MarketingServiceBaseUrl']  = $serviceUrls['MarketingService']
    $cfg['Services:PointServiceBaseUrl']       = $serviceUrls['PointService']
    $cfg['Services:CartServiceBaseUrl']        = $serviceUrls['CartService']
    $cfg['Services:OrderServiceBaseUrl']       = $serviceUrls['OrderService']
    $cfg['Services:PaymentServiceBaseUrl']     = $serviceUrls['PaymentService']
    $cfg['Services:EvaluateServiceBaseUrl']    = $serviceUrls['EvaluateService']

    # 自提取货码的 RSA 私钥路径。密钥文件由 ./scripts/generate-pickup-rsa.ps1 生成，
    # 不入库、不进发布包；这里只给路径。取货码 = 公钥加密后的订单号，
    # 加解密全在服务端（用户明确要求私钥不放前端）。
    if ($name -eq 'OrderService') {
        $cfg['PickupCode:RsaPrivateKeyPath'] = 'D:/学习/SimpleShop-new/deploy/keys/pickup-code-private.pem'
        $cfg['PickupCode:RsaPublicKeyPath']  = 'D:/学习/SimpleShop-new/deploy/keys/pickup-code-public.pem'

        # 支付超时关单（BUSINESS.md 7.3）：超时 30 分钟。
        # 阈值放配置而不是写死：不同业务等待时长不同，线上要临时调长时改配置比发版快得多。
        $cfg['Orders:PaymentTimeoutMinutes'] = '30'
        $cfg['Orders:ScanBatchSize']         = '200'
        $cfg['Orders:MaxClosePerRun']        = '500'
    }
    if ($name -eq 'CustomerService') {
        $cfg['Jwt:Issuer']       = 'simpleshop'
        $cfg['Jwt:Audience']     = 'simpleshop-customer'
        $cfg['Jwt:Secret']       = $customerJwtSecret
        $cfg['Jwt:ExpireHours']  = '12'
    }
    if ($name -eq 'Gateway') {
        # 网关配置。路由表在 ocelot.json（随代码发布），这里只放密钥与下游地址。
        # InternalToken 必须与所有下游的 Tenancy:InternalToken 一致——
        # 网关是唯一合法的写入方，下游只认这个口令才采信 X-Claim-*。
        $cfg['Gateway:InternalToken'] = $internalToken

        # 后台令牌：与 AuthService 同一份证书公钥、同一签发方
        $cfg['Gateway:AdminToken:Issuer'] = 'https://simpleshop.local/'
        $cfg['Gateway:AdminToken:SigningCertificatePath'] = 'D:/学习/SimpleShop-new/deploy/certs/signing.pfx'

        # 客户令牌：与 CustomerService 同一个 HS256 密钥、同一个签发方
        $cfg['Gateway:CustomerToken:Issuer'] = 'simpleshop-customer'
        $cfg['Gateway:CustomerToken:Secret'] = $customerJwtSecret

        # RBAC：路径 → 权限点映射从权限中心拉，缓存 30 秒
        $cfg['Gateway:Rbac:PermissionServiceUrl'] = $serviceUrls['PermissionService']
        $cfg['Gateway:Rbac:CacheSeconds'] = '30'
        # 拉不到映射时默认**拒绝**（fail-closed）：宁可全站 503，也不能让权限中心一挂
        # 全站就人人都是超管。
        $cfg['Gateway:Rbac:AllowAllWhenUnavailable'] = 'false'

        # 匿名白名单：登录 / 注册 / 换令牌 + **前台商品只读**。
        # 不写黑名单——黑名单意味着「忘了加的接口默认放行」，等于没做鉴权。
        #
        # 商品浏览为什么必须匿名：用户没登录就想先看看有什么，这是最正常的行为。
        # 要求先登录再逛商品，会把大量潜在用户挡在第一步之外。
        # 这里放行的只读接口本身就过滤了「审核通过 + 已上架」，不存在越权风险；
        # 真正需要登录的加购 / 下单不在这个前缀下。
        $cfg['Gateway:AnonymousPaths:0'] = '/gateway/customers/Register'
        $cfg['Gateway:AnonymousPaths:1'] = '/gateway/customers/Login'
        $cfg['Gateway:AnonymousPaths:2'] = '/gateway/auth/Token'
        $cfg['Gateway:AnonymousPaths:3'] = '/gateway/shop/products/List'
        $cfg['Gateway:AnonymousPaths:4'] = '/gateway/shop/products/Detail'
        $cfg['Gateway:AnonymousPaths:5'] = '/gateway/shop/catalog/CategoryTree'
        $cfg['Gateway:AnonymousPaths:6'] = '/gateway/shop/catalog/Brands'
    }
    if ($name -eq 'AuthService') {
        # 后台令牌服务配置（BUSINESS 4.1 / DATA_SPEC 1.5）
        # 有效期是**服务端全局**设置，OpenIddict 7 的客户端描述符里没有有效期字段。
        # 必须是绝对 URI：OpenIddict SetIssuer 直接 new Uri(...) 校验，裸字符串会启动失败。
        $cfg['Auth:Issuer']        = 'https://simpleshop.local'
        $cfg['Auth:Audience']      = 'simpleshop-admin'
        $cfg['Auth:ClientId']      = 'admin-app'
        $cfg['Auth:AccessTokenHours'] = '2'
        $cfg['Auth:RefreshTokenDays'] = '7'
        # 签名证书路径。AuthService 与 Gateway 必须读同一份文件——换了证书所有已签发令牌立刻失效。
        # 证书本身不入库，由 ./scripts/generate-signing-cert.ps1 生成。
        $cfg['Auth:SigningCertificatePath'] = 'D:/学习/SimpleShop-new/deploy/certs/signing.pfx'
    }
    if ($name -eq 'ToolService') {
        # 文件存储配置（DATA_SPEC 3.4）。本地存储用于开发；上云只需改 Provider，
        # 业务代码通过 IFileStorage 抽象，不感知具体后端。
        $cfg['FileStorage:Provider']            = 'Local'
        $cfg['FileStorage:LocalRoot']           = 'D:/学习/SimpleShop-new/uploads'
        $cfg['FileStorage:PublicBase']          = '/gateway/files/Content'
        $cfg['FileStorage:AllowedExtensions']   = 'png,jpg,jpeg,gif,bmp,webp,pdf,doc,xls,ppt,txt'
        $cfg['FileStorage:MaxSizeBytes:image']      = '5242880'
        $cfg['FileStorage:MaxSizeBytes:document']   = '20971520'
        $cfg['FileStorage:MaxSizeBytes:audio']      = '20971520'
        $cfg['FileStorage:MaxSizeBytes:video']      = '209715200'
        $cfg['FileStorage:MaxSizeBytes:default']    = '10485760'
    }
    return $cfg
}

# ---------- Redis 库号分配：每个服务独占一个库 ----------
# 不能所有服务共用一个库：Redis 的 key 不带服务前缀，一旦同名 key（例如 "cache:home"）
# 出现在两个服务里就会互相覆盖，排查起来极难发现。所以这里显式一号一服务。
# Redis 默认 16 个库（0-15），正好装下：15 个真正用 Redis 的服务占 1~15，
# 0 号给目前不连 Redis 的网关。若后续再加服务，把 deploy/docker-compose 里
# redis 的 --databases 调到 32，否则这份表里的号会开始互相撞车。
$redisDbMap = [ordered]@{
    'CustomerService'         = 1
    'ScheduledService'        = 2
    'PermissionService'       = 3
    'UserService'             = 4
    'ToolService'             = 5
    'AuthService'             = 6
    'ProductService'          = 7
    'CartService'             = 8
    'InventoryService'        = 9
    'OrderService'            = 10
    'PaymentService'          = 11
    'MarketingService'        = 12
    'MerchantPlatformService' = 13
    'PointService'            = 14
    'EvaluateService'         = 15
    # 网关目前**不**真的用 Redis（不分配雪花 workerId，也不做 Redis 缓存）。
    # 把 0 号给它而不是留作「调试库」：15 个真正用 Redis 的服务刚好占满 1~15，
    # 而把一个用不上的号留给一个用不上的服务，是唯一不会浪费的分配方式。
    'Gateway'                 = 0
}

if ($Service) {
    $serviceMap = [ordered]@{}
    foreach ($s in $Service) {
        # LogService 是唯一的例外：它不连 Redis，不需要库号。
        if ($s -eq 'LogService') { $serviceMap[$s] = 0; continue }
        if (-not $redisDbMap.Contains($s)) { throw "服务 $s 未分配 Redis 库号，请先在 redisDbMap 中登记。" }
        $serviceMap[$s] = $redisDbMap[$s]
    }
} else {
    $serviceMap = $redisDbMap
}

# LogService 不在 redisDbMap 里（它根本不连 Redis），所以不在上面那轮循环里被播种。
# 单独补一次：它需要 LogIndex / EventConsumer / DeadLetter 三段配置才能启动。
if (-not $serviceMap.Contains('LogService')) { $serviceMap['LogService'] = 0 }

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




