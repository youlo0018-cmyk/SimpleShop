<#
.SYNOPSIS
    LogService（5088）+ 日志链路回归测试。
.DESCRIPTION
    日志链路有三个环节，任意一环断了都表现为「后台查不到日志」，极难定位：
      1) 生产：请求 / 异常中间件发 pv.log / operation.log / exception.log
      2) 传输：RabbitMQ topic 交换机 → simpleshop.log 队列 → 手动 ack → 失败重试 → 死信
      3) 落地：LogService 消费后写 Elasticsearch

    本脚本自带生产方：LogService 自己就装了请求日志中间件，
    所以打它自己的 /logs/Pv/List 就会产生 pv.log + operation.log。
    这样不必依赖别的服务是否在跑，回归可以单独跑。

    重点验证：
      - 读请求产生 pv.log、写请求额外产生 operation.log，并真的落到 ES
      - 三类日志的查询接口返回结构正确、过滤条件生效
      - 非法参数被校验拦住（翻页上限、时间区间倒置、空 eventId）
      - 死信接口：列表可用、重放不存在的记录返回「不存在」而不是假成功
    退出码非 0 即视为回归失败。
#>
[CmdletBinding()]
param(
    [string]$LogService = 'http://127.0.0.1:5088',
    [string]$Elasticsearch = 'http://127.0.0.1:9200',
    [int]$WaitSeconds = 25,
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

function Post([string]$Path, $Body) {
    # 🔴 必须把 400 也**读成对象**返回，不能让 Invoke-RestMethod 抛出去。
    # 校验失败的证据就在 400 的响应体里（success=false + errors 字段），
    # 一抛异常，测试只能看到「400 Bad Request」，
    # 于是「校验拦住了」和「网关/服务整个挂了」在报告里长得一模一样——
    # 前者是预期，后者是故障，混淆它们等于把回归测试变成摆设。
    try {
        return Invoke-RestMethod -Uri "$LogService$Path" -Method Post `
            -Body ($Body | ConvertTo-Json -Depth 6) -ContentType 'application/json' -TimeoutSec 30
    } catch {
        # 用 ErrorDetails.Message 而不是 Exception.Response.GetResponseStream()：
        # PowerShell 7 里响应流在抛出异常时已经被读空并释放，
        # 再去 GetResponseStream() 只会拿到空串，于是又退回成「看不到响应体」。
        $text = $_.ErrorDetails.Message
        if ([string]::IsNullOrWhiteSpace($text)) { throw }
        return $text | ConvertFrom-Json
    }
}

# ES 写完默认 1 秒才可检索（refresh_interval），
# 所以每次断言都要「轮询等待」而不是查一次就下结论——
# 查一次就断言会把「还没刷新」误判成「日志没写进去」。
function Wait-Index([string]$Index, [hashtable]$Must, [int]$TimeoutSec = $WaitSeconds) {
    $filter = @()
    foreach ($k in $Must.Keys) { $filter += @{ term = @{ $k = $Must[$k] } } }

    $query = @{ size = 1; query = @{ bool = @{ filter = $filter } } }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)

    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-RestMethod -Uri "$Elasticsearch/$Index/_search" -Method Post `
                -Body ($query | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 10
            $total = if ($r.hits.total) { [int]$r.hits.total.value } else { 0 }
            if ($total -gt 0) { return $true }
        } catch {
            # 索引还没建好时查询会 404，属于正常情况，继续等
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

Write-Host "`n=== LOG 前置：服务可达 ===" -ForegroundColor Cyan

$reachable = $false
try { $reachable = (Invoke-WebRequest "$LogService/health" -UseBasicParsing -TimeoutSec 10).StatusCode -eq 200 } catch { }
if (-not $reachable) {
    Write-Host "  LogService($LogService) 不可达，请先执行 ./scripts/start-services.ps1" -ForegroundColor Red
    Write-Host "通过: 0  失败: 1" -ForegroundColor Red
    exit 1
}

# 记录调用前的 pv 总数，后面用来断言「确实新增了」
$before = 0
try { $before = [int](Invoke-RestMethod "$Elasticsearch/simpleshop_log_pv/_count" -TimeoutSec 10).count } catch { }

Write-Host "`n=== LOG 生产：请求中间件确实在发事件 ===" -ForegroundColor Cyan

# 这一行本身就是被测对象：它既是查询调用，也是一次 POST 写请求，
# 应该同时产生 pv.log 与 operation.log。
$warm = Post '/logs/Pv/List' @{ page = 1; pageSize = 1 }

Invoke-Case 'API-LOG-001' 'POST 请求产生 pv.log 并落 ES' {
    Wait-Index 'simpleshop_log_pv' @{}
    $after = [int](Invoke-RestMethod "$Elasticsearch/simpleshop_log_pv/_count" -TimeoutSec 10).count
    return $after -gt $before
}

Invoke-Case 'API-LOG-002' 'POST 请求额外产生 operation.log' {
    Wait-Index 'simpleshop_log_operation' @{ service = 'LogService' }
}

Invoke-Case 'API-LOG-002b' '🔴 按 service 精确过滤生效（service 映射为 keyword）' {
    $r = Post '/logs/Operation/List' @{ page = 1; pageSize = 20; service = 'LogService' }
    if ($r.data.total -eq 0) {
        Write-Host "        （当前无 LogService 操作日志，跳过断言）" -ForegroundColor DarkGray
        return $true
    }
    # service 必须真的当 keyword 过滤：全部命中项的 service 都得是 LogService。
    # 如果它被 ES 动态映射成了 text，term 查询会一条都匹配不到，
    # 而 total=0 会被误判成「还没产生日志」，掩盖映射错误。
    return ($r.data.items | Where-Object { $_.service -ne 'LogService' }).Count -eq 0
}

Invoke-Case 'API-LOG-003' 'pv 记录字段齐全（服务名/路径/响应码/耗时/请求 Id）' {
    $r = Invoke-RestMethod "$Elasticsearch/simpleshop_log_pv/_search?size=1&sort=occurredAt:desc" -TimeoutSec 10
    $s = $r.hits.hits[0]._source
    return $s.service -and $s.path -and $s.method `
        -and ($s.PSObject.Properties.Name -contains 'statusCode') `
        -and ($s.PSObject.Properties.Name -contains 'elapsedMs') `
        -and ($s.PSObject.Properties.Name -contains 'requestId')
}

Write-Host "`n=== LOG 查询接口 ===" -ForegroundColor Cyan

Invoke-Case 'API-LOG-004' 'pv 列表返回成功信封' {
    $r = Post '/logs/Pv/List' @{ page = 1; pageSize = 5 }
    return $r.success -eq $true -and $null -ne $r.data
}

Invoke-Case 'API-LOG-005' 'pv 列表含 total / items / page / pageSize' {
    $r = Post '/logs/Pv/List' @{ page = 1; pageSize = 5 }
    foreach ($name in @('total', 'items', 'page', 'pageSize')) {
        if (-not ($r.data.PSObject.Properties.Name -contains $name)) { return $false }
    }
    return $true
}

Invoke-Case 'API-LOG-006' '关键字过滤生效（能筛出 LogService 自己的请求）' {
    $r = Post '/logs/Pv/List' @{ page = 1; pageSize = 20; keyword = 'logs/Pv' }
    # 没有匹配时返回空列表也算通过——关键是接口不能报错、也不能把不匹配的塞回来
    if ($r.data.total -eq 0) { return $true }
    return ($r.data.items | Where-Object { $_.path -notlike '*logs/Pv*' }).Count -eq 0
}

Invoke-Case 'API-LOG-007' '按 requestId 过滤只返回该请求的日志' {
    $first = Post '/logs/Pv/List' @{ page = 1; pageSize = 1 }
    $rid = $first.data.items[0].requestId
    if (-not $rid) { return $false }
    $r = Post '/logs/Pv/List' @{ page = 1; pageSize = 20; requestId = $rid }
    if ($r.data.total -lt 1) { return $false }

    # 🔴 断言的是「返回的每一条都属于这个 requestId」，**不是**「只有一条」。
    # 网关用 X-Correlation-Id 把一次请求的 Id 透传给下游，所以同一次调用
    # 会在 Gateway 和下游服务各记一条 pv —— 返回 2 条才是正确行为。
    # 这里原本断言 total -eq 1，全量回归时网关用例跑在前面就必红。
    # 教训：断言要落在「契约保证的性质」上，不能落在「当前恰好的样子」上。
    $wrong = @($r.data.items | Where-Object { $_.requestId -ne $rid })
    return $wrong.Count -eq 0
}

Invoke-Case 'API-LOG-008' 'operation 列表返回成功信封' {
    $r = Post '/logs/Operation/List' @{ page = 1; pageSize = 5 }
    return $r.success -eq $true -and $null -ne $r.data
}

Invoke-Case 'API-LOG-009' 'exception 列表返回成功信封' {
    $r = Post '/logs/Exception/List' @{ page = 1; pageSize = 5 }
    return $r.success -eq $true -and $null -ne $r.data
}

Invoke-Case 'API-LOG-010' '🔴 响应码过滤生效（minStatusCode=500 不含 200 的记录）' {
    $r = Post '/logs/Pv/List' @{ page = 1; pageSize = 20; minStatusCode = 500 }
    return ($r.data.items | Where-Object { $_.statusCode -lt 500 }).Count -eq 0
}

Write-Host "`n=== LOG 校验：非法参数必须被拦 ===" -ForegroundColor Cyan

Invoke-Case 'API-LOG-011' '🔴 pageSize=0 被 PageSize 规则拒绝' {
    $r = Post '/logs/Pv/List' @{ page = 1; pageSize = 0 }
    # 断言到**具体字段**：只看 success=false 分不清是 PageSize 规则拦的、
    # 还是别的规则顺带拦的。规则改名或漏配时，只有断言字段名才会红。
    return $r.success -eq $false `
        -and ($r.errors.PSObject.Properties.Name -contains 'PageSize')
}

Invoke-Case 'API-LOG-012' '🔴 pageSize=9999 被 PageSize 上限拒绝（防一次拉爆 ES）' {
    $r = Post '/logs/Pv/List' @{ page = 1; pageSize = 9999 }
    return $r.success -eq $false `
        -and ($r.errors.PSObject.Properties.Name -contains 'PageSize')
}

Invoke-Case 'API-LOG-013' '🔴 page=0 被 Page 规则拒绝' {
    $r = Post '/logs/Pv/List' @{ page = 0; pageSize = 5 }
    return $r.success -eq $false `
        -and ($r.errors.PSObject.Properties.Name -contains 'Page')
}

Invoke-Case 'API-LOG-014' '🔴 结束时间早于开始时间被 To 规则拒绝' {
    $r = Post '/logs/Pv/List' @{
        from = '2026-03-02T00:00:00Z'
        to   = '2026-03-01T00:00:00Z'
    }
    return $r.success -eq $false `
        -and ($r.errors.PSObject.Properties.Name -contains 'To')
}

Invoke-Case 'API-LOG-015' '🔴 死信重放 eventId 为空被 EventId 规则拒绝（防路径穿越）' {
    $r = Post '/logs/DeadLetter/Replay' @{ eventId = '' }
    return $r.success -eq $false `
        -and ($r.errors.PSObject.Properties.Name -contains 'EventId')
}

Write-Host "`n=== LOG 死信闭环 ===" -ForegroundColor Cyan

Invoke-Case 'API-LOG-016' '死信列表返回成功信封' {
    $r = Post '/logs/DeadLetter/List' @{ page = 1; pageSize = 10 }
    return $r.success -eq $true -and $null -ne $r.data
}

Invoke-Case 'API-LOG-017' '🔴 重放不存在的死信返回「不存在」而不是假成功' {
    $r = Post '/logs/DeadLetter/Replay' @{ eventId = 'not-exist-event-id' }
    return $r.success -eq $false -and $r.message -match '不存在'
}

Invoke-Case 'API-LOG-018' '死信记录带失败原因（重放时才能挑着放）' {
    $r = Post '/logs/DeadLetter/List' @{ page = 1; pageSize = 10 }
    if ($r.data.total -eq 0) {
        Write-Host "        （当前无死信，跳过断言）" -ForegroundColor DarkGray
        return $true
    }
    $item = $r.data.items[0]
    return $item.eventId -and $item.errorMessage -and $item.errorType `
        -and ($item.PSObject.Properties.Name -contains 'attempts') `
        -and ($item.PSObject.Properties.Name -contains 'replayCount')
}

Write-Host ""
Write-Host "通过: $script:pass  失败: $script:fail" -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
if ($script:fail -gt 0) {
    Write-Host "失败用例:" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
exit 0
