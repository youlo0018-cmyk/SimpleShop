<#
.SYNOPSIS
    秒杀抢购并发压测（PLAN.md S7 验收步骤 1、2）。
.DESCRIPTION
    秒杀防超卖方案唯一的验收手段。**单线程顺序请求测不出超卖**——
    超卖只在并发下才发生，所以这两条不能省，也不能用普通回归脚本顺带跑：

      1. 库存 10、200 并发抢 → **恰好 10 个订单**
      2. 同一人 200 并发   → **恰好 1 个订单**

    单独一个脚本的原因：200 个 `Start-ThreadJob` 线程开销很大，
    塞进 marketing-regression.ps1 会让日常回归从 2 秒变成 1 分钟。
    常规回归只跑 30 / 10 并发（`API-SKL-030` / `API-SKL-031`），
    真正验收时用本脚本压 200。

    退出码非 0 即视为回归失败。
#>
[CmdletBinding()]
param(
    [string]$Gateway = 'http://127.0.0.1:5008',
    [string]$Marketing = 'http://127.0.0.1:5072',
    [string]$AdminUser = 'codexadmin',
    [string]$AdminPassword = 'Admin123456',
    [int]$Concurrency = 200,
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
$script:categoryIds = @()
$script:productId = 0
$script:skuId = 0

# ---- 后台令牌（建商品要走网关带令牌）----
Add-Type -AssemblyName System.Net.Http
$http = [System.Net.Http.HttpClient]::new()
$dd = [System.Collections.Generic.Dictionary[string, string]]::new()
$dd['grant_type'] = 'password'
$dd['client_id'] = 'admin-app'
$dd['username'] = $AdminUser
$dd['password'] = $AdminPassword
$tokenJson = ($http.PostAsync(
    "$Gateway/gateway/auth/token",
    [System.Net.Http.FormUrlEncodedContent]::new($dd)
).GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult()) | ConvertFrom-Json
$script:adminHeaders = @{ Authorization = "Bearer $($tokenJson.access_token)" }

function New-SklSession {
    $now = [DateTime]::UtcNow
    return (Invoke-RestMethod "$Marketing/marketing/seckill/sessions/Create" -Method Post `
        -Body (@{
            sessionName = "压测场次$($script:suffix)"; platformId = 0; merchantId = 0
            startTime = $now.AddHours(-1).ToString('o'); endTime = $now.AddHours(2).ToString('o')
            sortOrder = 0
        } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30).data
}

function Publish-Skl([long]$Id) {
    return Invoke-RestMethod "$Marketing/marketing/seckill/sessions/Publish" -Method Post `
        -Body (@{ sessionId = $Id; force = $false } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 60
}

function Get-ItemRow([long]$SessionId, [long]$ItemId) {
    $list = Invoke-RestMethod "$Marketing/marketing/seckill/sessions/Items/List" -Method Post `
        -Body (@{ sessionId = $SessionId } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    return @($list.data | Where-Object { $_.itemId -eq "$ItemId" })[0]
}

function Stop-Skl([long]$Id) {
    try {
        Invoke-RestMethod "$Marketing/marketing/seckill/sessions/Finish" -Method Post `
            -Body (@{ sessionId = $Id; cancel = $true } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 60 | Out-Null
    } catch {
        Write-Host ("        （结束场次失败：" + $_.Exception.Message + "）") -ForegroundColor DarkGray
    }
}

Write-Host "`n=== 准备 ===" -ForegroundColor Cyan

Invoke-Case 'CONC-000' "建一个 400 件库存、单价 200 的 SKU（供两个场次各划 10）" {
    $a = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = 0; categoryName = "压测$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $b = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = $a; categoryName = "压测$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $c = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:adminHeaders `
        -Body (@{ parentId = $b; categoryName = "压测$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $script:categoryIds = @($a, $b, $c)

    $body = @{
        productId = 0; spuName = "压测商品$($script:suffix)"; categoryId = $c
        deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
        specs = @(@{ specName = '颜色'; specValues = @('红') })
        skus = @(@{ skuCode = "CN$($script:suffix)"; specValues = @('红'); price = 200; stock = 400; status = 1 })
    }
    $script:productId = [long](Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:adminHeaders `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 60).data

    # 🔴 商品必须「审核通过 + 已上架」才可下单（BUSINESS.md 14.4「商品需审核后上架」）。
    # 秒杀单同样走下单链路，回查时一样校验审核与上架状态。
    # 这个脚本不在 run-all.ps1 里（200 线程太重），所以很容易在补这条时漏掉它，
    # 表现是「200 个并发全部落到『其它』」，看着像超卖防护失效，其实是全部被拒。
    (Invoke-RestMethod "$Gateway/gateway/products/Audit" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $script:productId; auditStatus = 20 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30) | Out-Null
    (Invoke-RestMethod "$Gateway/gateway/products/ChangeListing" -Method Post -Headers $script:adminHeaders `
        -Body (@{ productId = $script:productId; status = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30) | Out-Null

    $det = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:productId)" `
        -Headers $script:adminHeaders -TimeoutSec 30
    $script:skuId = [long]$det.data.skus[0].id
    return $script:skuId -gt 0
}

Write-Host "`n=== 验收 1：库存 10、$Concurrency 并发抢 → 恰好 10 个订单 ===" -ForegroundColor Cyan
$script:s1 = 0
$script:i1 = 0

Invoke-Case 'CONC-001' "$Concurrency 个不同客户并发抢 10 件：成功恰好 10，其余全部「已抢完」" {
    $script:s1 = [long](New-SklSession)
    $script:i1 = [long](Invoke-RestMethod "$Marketing/marketing/seckill/sessions/Items/Add" -Method Post `
        -Body (@{ sessionId = $script:s1; skuId = $script:skuId; seckillPrice = 66.00; seckillStock = 10; perUserLimit = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    if (-not (Publish-Skl $script:s1).success) { return $false }

    $jobs = 1..$Concurrency | ForEach-Object {
        $cid = 940000000 + $script:suffix + $_
        Start-ThreadJob -ScriptBlock {
            param($url, $item, $customer)
            try {
                $r = Invoke-RestMethod -Uri $url -Method Post `
                    -Body (@{
                        itemId = $item; customerId = $customer
                        receiverName = '压测'; receiverPhone = '13800138000'
                        receiverAddress = '测试省测试市测试区 1 号'
                    } | ConvertTo-Json) `
                    -ContentType 'application/json' -TimeoutSec 60
                [string]$r.data.resultStatus
            } catch {
                # 把真实原因带回来：只回 'ERR' 的话，200 个并发全部失败时
                # 只能看到「异常 0 / 其它 200」，既不像超卖也不像限流，无从下手。
                # 曾经就是这样把「商品没过审被全部拒单」误判成「超卖防护失效」。
                $raw = $_.ErrorDetails.Message
                if ([string]::IsNullOrWhiteSpace($raw)) { return 'ERR' }
                try { return "ERR:$(($raw | ConvertFrom-Json).message)" } catch { return 'ERR' }
            }
        } -ArgumentList "$Marketing/marketing/seckill/grab", $script:i1, $cid
    }

    $done = $jobs | Wait-Job -Timeout 600 | Receive-Job
    $jobs | Remove-Job -Force

    # resultStatus：1 成功 / 2 已抢完 / 3 不在抢购中 / 4 超限购
    $ok = @($done | Where-Object { $_ -eq '1' }).Count
    $soldOut = @($done | Where-Object { $_ -eq '2' }).Count
    $limited = @($done | Where-Object { $_ -eq '4' }).Count
    $err = @($done | Where-Object { $_ -like 'ERR*' }).Count
    $other = @($done | Where-Object { $_ -notin @('1', '2', '4') -and $_ -notlike 'ERR*' }).Count

    $row = Get-ItemRow $script:s1 $script:i1
    Write-Host ("        成功 {0} / 抢完 {1} / 超限购 {2} / 异常 {3} / 其它 {4}" -f `
        $ok, $soldOut, $limited, $err, $other) -ForegroundColor DarkGray
    Write-Host ("        soldCount={0} remaining={1} seckillStock={2}" -f `
        $row.soldCount, $row.remaining, $row.seckillStock) -ForegroundColor DarkGray

    # 失败时把出现过的错误原文打出来，否则「异常 200」是个没有线索的数字。
    if ($err -gt 0) {
        Write-Host ("        错误样本: " + (($done | Where-Object { $_ -like 'ERR*' } |
            Group-Object | Select-Object -First 3 | ForEach-Object { "$($_.Count)x $($_.Name)" }) -join ' | ')) `
            -ForegroundColor DarkYellow
    }

    $ok -eq 10 -and $soldOut -eq ($Concurrency - 10) -and $err -eq 0 -and $other -eq 0 `
        -and $row.soldCount -eq 10 -and $row.remaining -eq 0
}

Write-Host "`n=== 验收 2：同一客户 $Concurrency 并发 → 恰好 1 个订单 ===" -ForegroundColor Cyan
$script:s2 = 0
$script:i2 = 0

Invoke-Case 'CONC-002' "同一客户 $Concurrency 并发：只允许 1 单成功，其余全部「超限购」" {
    $script:s2 = [long](New-SklSession)
    $script:i2 = [long](Invoke-RestMethod "$Marketing/marketing/seckill/sessions/Items/Add" -Method Post `
        -Body (@{ sessionId = $script:s2; skuId = $script:skuId; seckillPrice = 55.00; seckillStock = $Concurrency; perUserLimit = 1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    if (-not (Publish-Skl $script:s2).success) { return $false }

    $cid = 950000000 + $script:suffix
    $jobs = 1..$Concurrency | ForEach-Object {
        Start-ThreadJob -ScriptBlock {
            param($url, $item, $customer)
            try {
                $r = Invoke-RestMethod -Uri $url -Method Post `
                    -Body (@{
                        itemId = $item; customerId = $customer
                        receiverName = '压测'; receiverPhone = '13800138000'
                        receiverAddress = '测试省测试市测试区 1 号'
                    } | ConvertTo-Json) `
                    -ContentType 'application/json' -TimeoutSec 60
                "$($r.data.resultStatus)|$($r.data.orderNo)"
            } catch {
                $raw = $_.ErrorDetails.Message
                if ([string]::IsNullOrWhiteSpace($raw)) { return 'ERR' }
                try { return "ERR:$(($raw | ConvertFrom-Json).message)" } catch { return 'ERR' }
            }
        } -ArgumentList "$Marketing/marketing/seckill/grab", $script:i2, $cid
    }

    $done = $jobs | Wait-Job -Timeout 600 | Receive-Job
    $jobs | Remove-Job -Force

    # 超限购的返回形如 "4|"（没下单，订单号为空）——用 -eq '4' 匹配不到
    $ok = @($done | Where-Object { $_ -like '1|*' }).Count
    $limited = @($done | Where-Object { $_ -like '4|*' }).Count
    $err = @($done | Where-Object { $_ -like 'ERR*' }).Count
    # 不能写 -notin @('1|*','4|*')：-notin 做的是**字面量**比较，不认通配符，
    # 会把全部结果都算成 other。这里用减法算「没落进任何已知分类的」。
    $classified = $ok + $limited + $err
    $other = @($done).Count - $classified

    # 两单并发都「成功」但拿到不同订单号，是最糟的情况，必须单独断言唯一性
    $distinctOrders = @(
        $done | Where-Object { $_ -like '1|*' } |
            ForEach-Object { $_.Split('|')[1] } |
            Sort-Object -Unique
    ).Count

    $row = Get-ItemRow $script:s2 $script:i2
    Write-Host ("        成功 {0} / 超限购 {1} / 异常 {2} / 其它 {3}" -f `
        $ok, $limited, $err, $other) -ForegroundColor DarkGray
    Write-Host ("        不同订单号 {0} 个；soldCount={1}" -f $distinctOrders, $row.soldCount) -ForegroundColor DarkGray

    $ok -eq 1 -and $distinctOrders -eq 1 -and $limited -eq ($Concurrency - 1) `
        -and $err -eq 0 -and $other -eq 0 -and $row.soldCount -eq 1
}

Write-Host "`n=== 清理 ===" -ForegroundColor Cyan

Invoke-Case 'CONC-010' '结束两个场次（剩余库存回补常规池）' {
    Stop-Skl $script:s1
    Stop-Skl $script:s2
    return $true
}

Invoke-Case 'CONC-011' '删商品 → 删分类' {
    if ($script:productId -gt 0) {
        Invoke-RestMethod "$Gateway/gateway/products/Delete" -Method Post -Headers $script:adminHeaders `
            -Body (@{ productId = $script:productId } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 60 | Out-Null
    }
    foreach ($id in [array]($script:categoryIds | Sort-Object -Descending)) {
        Invoke-RestMethod "$Gateway/gateway/categories/Delete" -Method Post -Headers $script:adminHeaders `
            -Body (@{ categoryId = $id } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
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
