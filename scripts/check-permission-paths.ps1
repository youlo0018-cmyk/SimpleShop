<#
.SYNOPSIS
    静态核对：每条权限点的 api_path 是否真的对得上某个后端端点。
.DESCRIPTION
    网关 RBAC 的判定逻辑是「路径映射查不到 → requiredCode 为 null → **放行**」。
    所以 api_path 写错（多一层 / 少一层、动作名不对、根本没这个端点）不是「403 拒绝」，
    而是**接口彻底不鉴权**——任何登录用户都能调。这个缺陷静默、无日志、靠单测测不出来。
    本脚本做的是「不用起服务就能发现」的那一半：把 api_path 按 ocelot 规则翻成下游路径，
    再和 src 下真实控制器路由比对，对不上的直接列出来。
    通配（/*）只核对前缀那部分；路由缺失（ocelot 没配）也算问题。
#>
[CmdletBinding()]
param(
    [string]$Root,
    [switch]$AsError
)
$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
Push-Location $Root
try {
    # ---------- 1. 收集真实端点 ----------
    $endpoints = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $ctlFiles = Get-ChildItem 'src' -Recurse -Filter '*Controller.cs' -File |
        Where-Object { $_.DirectoryName -match '\.Api[\\/]Controllers$' }
    foreach ($f in $ctlFiles) {
        $text = [System.IO.File]::ReadAllText($f.FullName)
        $route = [regex]::Match($text, '\[Route\("([^"]+)"\)\]')
        $base = if ($route.Success) { $route.Groups[1].Value } else { '' }
        foreach ($m in [regex]::Matches($text, '\[Http(Post|Get|Put|Delete|Patch)\("([^"]*)"\)\]')) {
            $tail = $m.Groups[2].Value
            $path = '/' + $base
            if ($tail -ne '') { $path += '/' + $tail }
            [void]$endpoints.Add(($path -replace '/+', '/').TrimEnd('/'))
        }
    }
    # ---------- 2. 网关转发规则 ----------
    $ocelot = Get-Content 'src/Gateway/Gateway.Api/ocelot.json' -Raw | ConvertFrom-Json
    $rules = @($ocelot.Routes | ForEach-Object {
        [pscustomobject]@{ Up = [string]$_.UpstreamPathTemplate; Down = [string]$_.DownstreamPathTemplate }
    })
    function Resolve-Downstream([string]$gatewayPath) {
        # 与 RoutePermissionCache 同样的「最长前缀优先」规则。
        $best = $null
        $bestLen = -1
        $rest = ''
        foreach ($r in $rules) {
            $matched = $false
            $tail = ''
            if ($r.Up -match '^(.*)/\{(everything|\*)\}$') {
                $base = $Matches[1]
                if ($gatewayPath.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) {
                    $matched = $true
                    $tail = $gatewayPath.Substring($base.Length)
                }
            }
            elseif ($gatewayPath.TrimEnd('/').Equals($r.Up.TrimEnd('/'), [StringComparison]::OrdinalIgnoreCase)) {
                $matched = $true
            }
            if ($matched -and $r.Up.Length -gt $bestLen) { $best = $r; $bestLen = $r.Up.Length; $rest = $tail }
        }
        if ($null -eq $best) { return $null }
        $down = $best.Down
        if ($down -match '^(.*)/\{(everything|\*)\}$') {
            $downBase = $Matches[1]
            return ($downBase + $rest).TrimEnd('/')
        }
        return $down.TrimEnd('/')
    }
    function Check-Path([string]$code, [string]$name, [string]$apiPath) {
        $isWildcard = $apiPath.EndsWith('/*')
        $probe = $apiPath.TrimEnd('/')
        if ($isWildcard) { $probe = $probe.Substring(0, $probe.Length - 1) + '/__probe__' }
        $down = Resolve-Downstream $probe
        if ($null -eq $down) {
            # 通配 RBAC 路径不要求 ocelot 有**字面量**转发规则：
            # 网关 RBAC 是纯前缀匹配，与转发规则是两套机制。
            # 只要求「存在某条上游路由能覆盖这个前缀」，否则这条权限点确实够不着。
            if ($isWildcard) {
                $bare = $apiPath.Substring(0, $apiPath.Length - 1).TrimEnd('/')
                $covered = $rules | Where-Object {
                    $u = $_.Up.TrimEnd('/')
                    # 覆盖 = 有「恰好等于」的前缀路由，或有「落在这个前缀下面」的子路由。
                    # 只判前者会漏掉 /gateway/reports/* 这种：ocelot 里是四条字面量路由
                    # /gateway/reports/Report 等，没有 /*，但 RBAC 通配照样匹配得到。
                    $u.Equals($bare, [StringComparison]::OrdinalIgnoreCase) -or
                    $bare.StartsWith($u + '/', [StringComparison]::OrdinalIgnoreCase) -or
                    $u.StartsWith($bare + '/', [StringComparison]::OrdinalIgnoreCase)
                } | Select-Object -First 1
                if ($covered) { continue }
            }
            $problems.Add([pscustomobject]@{
                Code = $code; Name = $name; ApiPath = $apiPath
                Problem = '网关未配置该路径的转发规则，请求根本到不了后端'
            })
            continue
        }
        if ($isWildcard) {
            $prefix = $down.Substring(0, $down.LastIndexOf('/')).TrimEnd('/')
            $hit = $endpoints | Where-Object { $_ -like ($prefix + '/*') -or $_ -eq $prefix } | Select-Object -First 1
            if (-not $hit) {
                $problems.Add([pscustomobject]@{
                    Code = $code; Name = $name; ApiPath = $apiPath
                    Problem = "通配前缀下没有任何真实端点（下游 $prefix）"
                })
            }
            continue
        }
        if (-not $endpoints.Contains($down)) {
            $problems.Add([pscustomobject]@{
                Code = $code; Name = $name; ApiPath = $apiPath
                Problem = "下游 $down 没有对应端点 → 网关查不到映射，会放行（等于不鉴权）"
            })
        }
    }

    # ---------- 3. 收集权限点 ----------
    $seed = [System.IO.File]::ReadAllText('scripts/seed-permissions.ps1')
    $block = [regex]::Match($seed, '(?s)\$leaves\s*=\s*\[ordered\]@\{(.*?)\n\}')
    if (-not $block.Success) { throw 'seed-permissions.ps1 里找不到 $leaves 块' }
    $entries = [regex]::Matches(
        $block.Groups[1].Value,
        "@\('(?<code>[^']+)',\s*'(?<name>[^']+)',\s*'(?<path>[^']+)'\)")
    $problems = New-Object System.Collections.Generic.List[object]
    foreach ($e in $entries) {
        $code = $e.Groups['code'].Value
        $name = $e.Groups['name'].Value

        # 一个权限点可绑多条路径（逗号分隔，BUSINESS.md 5.4「ApiPath 可多个」）。
        # 这里必须**逐条**校验：只校验第一条会漏掉后面写错的那条，
        # 而漏掉的那条同样会变成「网关查不到映射 → 放行」。
        $apiPaths = $e.Groups['path'].Value -split '[,;]'
        foreach ($apiPath in $apiPaths) {
            $apiPath = $apiPath.Trim()
            if ($apiPath -eq '' -or -not $apiPath.StartsWith('/gateway/')) {
                $problems.Add([pscustomobject]@{
                    Code = $code; Name = $name; ApiPath = $apiPath
                    Problem = '路径必须以 /gateway/ 开头'
                })
                continue
            }
            Check-Path -code $code -name $name -apiPath $apiPath
        }
    }

    Write-Host ("==> 权限点 {0} 条 / 端点 {1} 个 / 路由 {2} 条" -f $entries.Count, $endpoints.Count, $rules.Count) -ForegroundColor Cyan

    # ---------- 4. 反向核对：网关可达的端点里，有没有**漏配**权限点的 ----------
    #
    # 上面那一半查的是「声明了的权限点对不对得上真实端点」。
    # 这一半查的是反面：**真实端点有没有被声明**。
    #
    # 为什么必须查这一半：RBAC 的判定是「查不到映射 → requiredCode 为 null → 放行」，
    # 所以漏配一条 api_path 就等于那个接口完全不鉴权。这正是
    # /gateway/payments/Simulate 的洞 —— 顾客拿客户令牌就能把自己的订单标成已支付。
    # 只看「已声明的路径对不对」永远发现不了它，因为漏配的东西压根不在声明里。
    #
    # 判定规则：网关可达的端点，要么在权限种子里有映射，要么在下面这份
    # **C 端 / 匿名白名单**里显式登记。白名单的意义是让「这个是故意不鉴权的」
    # 变成一次可评审的决定，而不是一次疏忽 —— 新增 C 端接口时补一行即可。
    # 注意这里写的是**下游路径**（控制器上的真实路由），不是网关路径：
    # $endpoints 收集的是控制器路由，网关前缀在这一步已经剥掉了。
    $cEndPaths = @(
        # 匿名白名单（与 AgileConfig 的 Gateway:AnonymousPaths 对应）
        '/customers/Register', '/customers/Login',
        '/shop/products/*', '/shop/catalog/*',
        '/design/Store', '/design/PlatformStore',
        '/regions/Public',
        '/evaluates/List', '/marketing/seckill/sessions/Public',
        '/coupons/Available',
        # C 端（客户令牌）—— 刻意不绑后台权限点，绑了会把小程序自己挡掉
        '/carts/*', '/orders/*', '/coupons/*', '/evaluates/*',
        '/points/*', '/merchants/Shop/*',
        # 客户资料 / 地址簿 / 收藏：都是「只能操作自己那份」，归属由 CustomerScope 在服务端校验
        '/customers/Profile', '/customers/UpdateProfile',
        '/customers/addresses/*', '/customers/favorites/*',
        # payments/Create|Confirm|Query 是客户付款；Simulate 是后台的，已单独绑 order:simulate
        '/payments/Create', '/payments/Confirm', '/payments/Query',
        # 小程序发表评价要传图，复用统一上传接口；网关按客户令牌放行，
        # 接口本身仍要求登录（见 GatewayOptions.CustomerAllowedPaths）
        '/files/Upload',
        # 客户申请退款；归属由 PaymentOwnership 在服务端校验，
        # 后台代客退款仍走同一个入口并持有 refund:apply 权限点
        '/refunds/Apply',
        '/marketing/activities/FinalPrice', '/marketing/activities/FinalPriceBatch',
        '/marketing/seckill/grab/result',
        # 报表：/reports/Point 是**客户自己的**积分报表（C 端积分页用），
        # 与后台的 /reports/Report 等不是一回事
        '/reports/Point'
    )
    function Test-CEnd([string]$downPath) {
        foreach ($p in $cEndPaths) {
            if ($p.EndsWith('/*')) {
                $b = $p.Substring(0, $p.Length - 2)
                if ($downPath -eq $b -or $downPath.StartsWith("$b/")) { return $true }
            }
            elseif ($downPath -eq $p) { return $true }
        }
        return $false
    }

    # 已声明的 api_path 翻成下游路径，供反向核对使用
    $declaredDown = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($e in $entries) {
        foreach ($apiPath in ($e.Groups['path'].Value -split '[,;]')) {
            $apiPath = $apiPath.Trim()
            if ($apiPath -eq '' -or -not $apiPath.StartsWith('/gateway/')) { continue }
            $isWildcard = $apiPath.EndsWith('/*')
            $probe = $apiPath.TrimEnd('/')
            if ($isWildcard) { $probe = $probe.Substring(0, $probe.Length - 1) + '/__probe__' }
            $down = Resolve-Downstream $probe
            if ($null -eq $down) { continue }
            if ($isWildcard) {
                $prefix = $down.Substring(0, $down.LastIndexOf('/')).TrimEnd('/')
                [void]$declaredDown.Add($prefix + '/*')
            }
            else { [void]$declaredDown.Add($down) }
        }
    }
    function Test-Declared([string]$downPath) {
        if ($declaredDown.Contains($downPath)) { return $true }
        foreach ($k in $declaredDown) {
            if ($k.EndsWith('/*')) {
                $b = $k.Substring(0, $k.Length - 2)
                if ($downPath -eq $b -or $downPath.StartsWith("$b/")) { return $true }
            }
        }
        return $false
    }

    $unmapped = New-Object System.Collections.Generic.List[object]
    foreach ($ep in $endpoints) {
        if ($ep -like '/internal*') { continue }
        # 网关根本转发不到的端点不算（例如只在服务内网暴露的）
        $gw = "/gateway" + $ep
        if ($null -eq (Resolve-Downstream $gw)) { continue }
        if (Test-CEnd $ep) { continue }
        if (Test-Declared $ep) { continue }
        $unmapped.Add($ep)
    }

    if ($unmapped.Count -gt 0) {
        Write-Host ("`n==> 发现 {0} 个网关可达端点**没有绑定任何权限点**（等于不鉴权）：" -f $unmapped.Count) -ForegroundColor Red
        foreach ($u in $unmapped) {
            Write-Host ("  {0}  → 在 seed-permissions.ps1 里补 api_path，或登记进本脚本的 C 端白名单" -f $u) -ForegroundColor Red
        }
        $problems.Add([pscustomobject]@{
            Code = '(未绑定)'; Name = '-'; ApiPath = "$($unmapped.Count) 个端点"
            Problem = '网关可达但没有权限映射，且不在 C 端白名单里'
        })
    }

    if ($problems.Count -eq 0) {
        Write-Host '==> 全部 api_path 都能落到真实端点' -ForegroundColor Green
        Write-Host '==> 网关可达端点全部有权限映射或在 C 端白名单里' -ForegroundColor Green
        exit 0
    }
    Write-Host ("`n==> 发现 {0} 条对不上的 api_path（这些接口等于**没有鉴权**）：" -f $problems.Count) -ForegroundColor Red
    foreach ($p in $problems) {
        Write-Host ("  [{0}] {1}  {2}" -f $p.Code, $p.ApiPath, $p.Problem) -ForegroundColor Red
    }
    if ($AsError) { exit 1 }
    exit 2
}
finally {
    Pop-Location
}
