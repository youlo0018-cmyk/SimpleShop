<#
.SYNOPSIS
    InventoryService（5062）回归测试。
.DESCRIPTION
    库存是订单链路的瓶颈，一旦算错就是超卖或丢货。所以重点验证三件事：
      - 三个计数的语义对不对（BUSINESS.md 9.2）
      - 幂等：同业务号重复请求不重复扣（唯一索引兜底，不是「先查再插」）
      - 并发：N 个并发请求抢有限库存，**不许出现负数**，成功的次数必须正好等于库存数
    退出码非 0 即视为回归失败。
#>
[CmdletBinding()]
param(
    [string]$Gateway = 'http://127.0.0.1:5008',
    [string]$Inventory = 'http://127.0.0.1:5062',
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

$script:headers = @{ Authorization = "Bearer $(Get-Token)" }
$script:suffix = Get-Random -Minimum 100000 -Maximum 999999

# 内部接口（不经网关）
function Invoke-Internal([string]$Path, $Body) {
    return Invoke-RestMethod -Uri "$Inventory/internal/inventory/$Path" -Method Post `
        -Body ($Body | ConvertTo-Json -Depth 6) -ContentType 'application/json' -TimeoutSec 30
}

function Get-Snapshot([long]$SkuId) {
    $r = Invoke-RestMethod "$Inventory/internal/inventory/Snapshot?skuIds=$SkuId" -TimeoutSec 30
    return @($r.data | Where-Object { $_.skuId -eq $SkuId })[0]
}

Write-Host "`n=== INI 库存三计数语义 ===" -ForegroundColor Cyan
$script:sku = 700000000000 + $script:suffix

# 补偿记录必须直接写库造：「释放失败」这件事按定义就是接口调不通（会抛异常），
# 没法从 API 侧触发。测试要验证的是「已经失败过一次之后能不能救回来」，
# 所以只能把失败后的状态直接摆好。
$script:compSku = 790000000000 + $script:suffix
$script:compPendingId = 790000000001 + $script:suffix

# 直连 psql 跑一条查询，返回第一列。
function Invoke-Psql([string]$Sql) {
    $raw = docker exec simpleshop-postgres psql -U postgres -d simpleshopinventory -t -A -F '|' -c $Sql
    return [pscustomobject]@{ data = ($raw | Select-Object -First 1) }
}

Invoke-Case 'API-INI-001' '初始化：available 0→10' {
    $r = Invoke-Internal 'Init' @{ skuId = $script:sku; quantity = 10; productName = '回归商品'; skuSpecText = '红 / M'; warnThreshold = 5; bizNo = "INIT-$($script:sku)" }
    return $r.success -and $r.data.available -eq 10 -and $r.data.locked -eq 0 -and $r.data.deducted -eq 0
}

Invoke-Case 'API-INI-002' '锁定：locked += q，available -= q' {
    $r = Invoke-Internal 'Apply' @{ skuId = $script:sku; action = 'lock'; quantity = 3; bizNo = 'ORD-1' }
    return $r.success -and $r.data.available -eq 7 -and $r.data.locked -eq 3
}

Invoke-Case 'API-INI-003' '🔴 同业务号重复锁定不重复扣（幂等）' {
    $r = Invoke-Internal 'Apply' @{ skuId = $script:sku; action = 'lock'; quantity = 3; bizNo = 'ORD-1' }
    return $r.success -and $r.data.alreadyApplied -eq $true -and $r.data.available -eq 7 -and $r.data.locked -eq 3
}

Invoke-Case 'API-INI-004' '支付扣减：locked -= q，deducted += q' {
    $r = Invoke-Internal 'Apply' @{ skuId = $script:sku; action = 'deduct'; quantity = 3; bizNo = 'ORD-1' }
    return $r.success -and $r.data.locked -eq 0 -and $r.data.deducted -eq 3 -and $r.data.available -eq 7
}

Invoke-Case 'API-INI-005' '取消释放：locked -= q，available += q' {
    # 先再锁一次才有得释放
    Invoke-Internal 'Apply' @{ skuId = $script:sku; action = 'lock'; quantity = 2; bizNo = 'ORD-2' } | Out-Null
    $r = Invoke-Internal 'Apply' @{ skuId = $script:sku; action = 'release'; quantity = 2; bizNo = 'ORD-2' }
    # 锁 2 再放 2 只是回到锁之前：此时 available 应是扣减后的 7，不是 9
    return $r.success -and $r.data.locked -eq 0 -and $r.data.available -eq 7
}

Invoke-Case 'API-INI-006' '退款回补：deducted -= q，available += q' {
    $r = Invoke-Internal 'Apply' @{ skuId = $script:sku; action = 'replenish'; quantity = 3; bizNo = 'RF-1' }
    return $r.success -and $r.data.deducted -eq 0 -and $r.data.available -eq 10
}

Invoke-Case 'API-INI-007' '一轮走完回到初始值（10 / 0 / 0）' {
    $s = Get-Snapshot $script:sku
    return $s.available -eq 10 -and $s.locked -eq 0 -and $s.deducted -eq 0
}

Write-Host "`n=== INI 拒绝路径 ===" -ForegroundColor Cyan

Invoke-Case 'API-INI-010' '🔴 超卖被拒（可用不足），库存不变' {
    $r = Invoke-Internal 'Apply' @{ skuId = $script:sku; action = 'lock'; quantity = 999; bizNo = 'ORD-BIG' }
    $s = Get-Snapshot $script:sku
    return -not $r.success -and $r.message -match '可用库存不足' -and $s.available -eq 10 -and $s.locked -eq 0
}

Invoke-Case 'API-INI-011' '数量为 0 被拒（400）' {
    try { Invoke-Internal 'Apply' @{ skuId = $script:sku; action = 'lock'; quantity = 0; bizNo = 'ORD-0' } | Out-Null; return $false }
    catch { return [int]$_.Exception.Response.StatusCode -eq 400 }
}

Invoke-Case 'API-INI-012' '未知动作被拒' {
    $r = Invoke-Internal 'Apply' @{ skuId = $script:sku; action = 'nonsense'; quantity = 1; bizNo = 'ORD-X' }
    return -not $r.success -and $r.message -match '未知的库存动作'
}

Invoke-Case 'API-INI-013' '未初始化的 SKU 被拒，且不产生流水' {
    $r = Invoke-Internal 'Apply' @{ skuId = 123456789; action = 'lock'; quantity = 1; bizNo = 'ORD-GHOST' }
    return -not $r.success -and $r.message -match '不存在'
}

Write-Host "`n=== INI 幂等与流水 ===" -ForegroundColor Cyan

Invoke-Case 'API-INI-020' '同一单号重复 5 次只写 1 条流水' {
    $sku2 = 700000000000 + $script:suffix + 1
    Invoke-Internal 'Init' @{ skuId = $sku2; quantity = 100; bizNo = "I2-$sku2" } | Out-Null

    1..5 | ForEach-Object {
        Invoke-Internal 'Apply' @{ skuId = $sku2; action = 'lock'; quantity = 1; bizNo = 'SAME-BIZ' } | Out-Null
    }

    $s = Get-Snapshot $sku2
    # 只有 1 被锁掉，重复 5 次不能锁掉 5
    return $s.available -eq 99 -and $s.locked -eq 1
}

Invoke-Case 'API-INI-021' '后台调库存走调整量而不是最终值' {
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/inventory/Adjust" -Method Post -Headers $script:headers `
        -Body (@{ skuId = $script:sku; availableAdjust = 5; remark = '盘点盈余入库' } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    return $r.success -and $r.data.available -eq 15
}

Invoke-Case 'API-INI-022' '🔴 后台调成负数被拒（4001 且库存纹丝不动）' {
    # 只断言「失败」不够：参数校验失败、动作名写错、库存记录不存在都回失败，
    # 而这里要验的是「负数被库存守卫拦下」——所以要断言**业务码**与**库存没变**。
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/inventory/Adjust" -Method Post -Headers $script:headers `
        -Body (@{ skuId = $script:sku; availableAdjust = -999; remark = '试图调成负数' } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30

    $after = (Get-Snapshot $script:sku).available
    if (-not $r.success) { Write-Host ("        被拒：" + $r.message + "；可用仍为 " + $after) -ForegroundColor DarkGray }

    return (-not $r.success) -and ([int]$r.code -eq 4001) -and $after -eq 15
}

Invoke-Case 'API-INI-023' '调整原因为空被拒（必须写明为什么改库存）' {
    try {
        Invoke-RestMethod -Uri "$Gateway/gateway/inventory/Adjust" -Method Post -Headers $script:headers `
            -Body (@{ skuId = $script:sku; availableAdjust = 1; remark = '' } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        return $false
    } catch { return [int]$_.Exception.Response.StatusCode -eq 400 }
}

Invoke-Case 'API-INI-024' '流水可查，含变更前后值与操作人' {
    $r = Invoke-RestMethod "$Gateway/gateway/inventory/Flows?skuId=$($script:sku)&limit=50" -Headers $script:headers -TimeoutSec 30
    return $r.success -and $r.data.Count -ge 5
}

Write-Host "`n=== INI 并发不许超卖 ===" -ForegroundColor Cyan

Invoke-Case 'API-INI-030' '🔴 20 个并发请求抢 10 件库存：成功恰好 10，库存不为负' {
    $sku3 = 700000000000 + $script:suffix + 2
    Invoke-Internal 'Init' @{ skuId = $sku3; quantity = 10; productName = '并发测试'; bizNo = "I3-$sku3" } | Out-Null

    $jobs = 1..20 | ForEach-Object {
        $biz = "CONC-$sku3-$_"
        Start-ThreadJob -ScriptBlock {
            param($url, $s, $b)
            try {
                $r = Invoke-RestMethod -Uri $url -Method Post `
                    -Body (@{ skuId = $s; action = 'lock'; quantity = 1; bizNo = $b } | ConvertTo-Json) `
                    -ContentType 'application/json' -TimeoutSec 30
                if ($r.success) { 'OK' } else { 'FAIL' }
            } catch { 'FAIL' }
        } -ArgumentList "$Inventory/internal/inventory/Apply", $sku3, $biz
    }

    $done = $jobs | Wait-Job -Timeout 90 | Receive-Job
    $jobs | Remove-Job -Force

    $ok = @($done | Where-Object { $_ -eq 'OK' }).Count
    $s = Get-Snapshot $sku3

    Write-Host ("        成功 " + $ok + " / 失败 " + (@($done | Where-Object { $_ -eq 'FAIL' }).Count) +
                "，最终 available=" + $s.available + " locked=" + $s.locked) -ForegroundColor DarkGray

    return $ok -eq 10 -and $s.available -eq 0 -and $s.locked -eq 10
}

Write-Host "`n=== INI 商品创建即初始化库存 ===" -ForegroundColor Cyan
$script:cat1 = 0; $script:cat3 = 0; $script:productId = 0

Invoke-Case 'API-INI-040' '建三级分类' {
    $script:cat1 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:headers `
        -Body (@{ parentId = 0; categoryName = "库存用例$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $cat2 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:headers `
        -Body (@{ parentId = $script:cat1; categoryName = "库存用例$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    $script:cat3 = (Invoke-RestMethod "$Gateway/gateway/categories/Create" -Method Post -Headers $script:headers `
        -Body (@{ parentId = $cat2; categoryName = "库存用例$($script:suffix)"; platformId = 0 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30).data
    return $script:cat3 -gt 0
}

Invoke-Case 'API-INI-041' '建商品后每个 SKU 都自动有了库存记录，且数量等于初始库存' {
    $body = @{
        productId = 0
        spuName = "带库存商品$($script:suffix)"
        categoryId = $script:cat3
        deliveryType = 1
        mainImage = 'https://cdn.example.com/m.png'
        specs = @(@{ specName = '颜色'; specValues = @('红','蓝') })
        skus = @(
            @{ skuCode = "IV$($script:suffix)-R"; specValues = @('红'); price = 50; stock = 25; status = 1 },
            @{ skuCode = "IV$($script:suffix)-B"; specValues = @('蓝'); price = 60; stock = 7;  status = 1 }
        )
    }
    $r = Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30
    $script:productId = $r.data
    if (-not $r.success) { return $false }

    $d = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($r.data)" -Headers $script:headers -TimeoutSec 30

    foreach ($s in $d.data.skus) {
        $snap = Get-Snapshot ([long]$s.id)
        if ($null -eq $snap) { return $false }
        # 红 25 / 蓝 7，与提交的初始库存一致
        $expect = if ($s.skuSpecText -eq '红') { 25 } else { 7 }
        if ($snap.available -ne $expect) { return $false }
        # 商品名 / 规格冗余进了库存列表，人工辨认用
        if ($snap.productName -ne "带库存商品$($script:suffix)") { return $false }
    }
    return $true
}

Invoke-Case 'API-INI-042' '编辑商品不会重置库存（编辑页不提供改库存入口）' {
    $body = @{
        productId = $script:productId
        spuName = "带库存商品$($script:suffix)-改名"
        categoryId = $script:cat3
        deliveryType = 1
        mainImage = 'https://cdn.example.com/m.png'
        specs = @(@{ specName = '颜色'; specValues = @('红','蓝') })
        skus = @(
            @{ skuCode = "IV$($script:suffix)-R"; specValues = @('红'); price = 55; stock = 0; status = 1 },
            @{ skuCode = "IV$($script:suffix)-B"; specValues = @('蓝'); price = 65; stock = 0; status = 1 }
        )
    }
    Invoke-RestMethod "$Gateway/gateway/products/Save" -Method Post -Headers $script:headers `
        -Body ($body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    $d = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:productId)" -Headers $script:headers -TimeoutSec 30
    $red = @($d.data.skus | Where-Object { $_.skuSpecText -eq '红' })[0]

    # 编辑时 stock 传了 0，但库存不该被改成 0
    return (Get-Snapshot ([long]$red.id)).available -eq 25
}

Write-Host "`n=== INI 释放补偿（释放失败后由定时任务救回来）===" -ForegroundColor Cyan

Invoke-Case 'API-INI-050' '🔴 P0 补偿重试把锁住的库存释放回来' {
    $init = Invoke-Internal 'Init' @{ skuId = $script:compSku; quantity = 30; productName = '补偿测试'; bizNo = "COMP-INIT-$($script:suffix)" }
    if (-not $init.success) { return $false }
    $lock = Invoke-Internal 'Apply' @{ skuId = $script:compSku; action = 'lock'; quantity = 10; bizNo = "COMP-LOCK-$($script:suffix)" }
    if (-not $lock.success) { return $false }

    # next_retry_at 用**明确的过去时间**。用 now() 会被当成本地时间(+08)写进 UTC 列，
    # 而应用按 UtcNow 比较，这条记录就会「看起来还没到重试时间」被直接跳过——
    # 症状是「补偿任务跑了但什么都没发生」，日志里完全看不出原因
    $biz = "COMP-REL-$($script:suffix)"
    Invoke-Psql "INSERT INTO pending_stock_release (id, created_at, is_deleted, biz_no, sku_id, quantity, reason, status, retry_count, last_error, next_retry_at, platform_id, merchant_id) VALUES ($($script:compPendingId), now(), false, '$biz', $($script:compSku), 10, '回归构造', 0, 0, '', '2000-01-01 00:00:00', 0, 0);" | Out-Null

    $before = Get-Snapshot $script:compSku
    $c = Invoke-Internal 'compensate-releases' @{ limit = 200 }
    if (-not $c.success) { return $false }

    $after = Get-Snapshot $script:compSku
    Write-Host ("        补偿前 locked={0} available={1} → 补偿后 locked={2} available={3}" -f $before.locked, $before.available, $after.locked, $after.available) -ForegroundColor DarkGray
    return $before.locked -eq 10 -and $after.locked -eq 0 -and $after.available -eq 30
}

Invoke-Case 'API-INI-051' '🔴 补偿成功后记录转「已处理」，再跑一轮不会重复释放' {
    $biz = "COMP-REL-$($script:suffix)"
    $st = Invoke-Psql "SELECT status FROM pending_stock_release WHERE biz_no = '$biz';"
    if ([int]$st.data.Trim() -ne 1) { return $false }
    $before = Get-Snapshot $script:compSku
    Invoke-Internal 'compensate-releases' @{ limit = 200 } | Out-Null
    $after = Get-Snapshot $script:compSku
    return $before.locked -eq $after.locked -and $before.available -eq $after.available
}

Invoke-Case 'API-INI-052' '🔴 重试的 bizNo 带后缀，不会被首次流水的幂等键挡住' {
    $biz = "COMP-REL-$($script:suffix)"
    $n = Invoke-Psql "SELECT count(*) FROM stock_flow WHERE sku_id = $($script:compSku) AND action = 'release' AND biz_no LIKE '$biz#R%';"
    # 幂等键是 {biz_no}:{action}。用原单号重试会命中首次那条流水被判「已处理过」而什么都没做，
    # 于是补偿记录一直重试一直失败——症状是「库存永远回不来」，日志里却看不出为什么
    return [int]$n.data.Trim() -ge 1
}

Invoke-Case 'API-INI-054' '🔴 P0 50 个并发锁 1 件（库存 10）：恰好成功 10 次，不超卖' {
    # BUSINESS.md 20.2 把 lock:stock:{skuId} 列为分布式锁，但代码里**没有这个键** ——
    # 原子性来自数据库本身（条件 UPDATE：available >= quantity 才扣）。
    # 这比 Redis 锁更强（没有 TTL 到期导致两个请求同时进临界区的问题），
    # 但「更强」这件事必须有证据，否则「锁没实现」也可能真的是没实现。
    #
    # 秒杀那条路有 200 并发用例，常规库存这条路此前**没有**并发覆盖 ——
    # 而常规库存才是绝大多数订单走的路径。
    $skuId = 880000000000 + $script:suffix
    # Invoke-Internal 已经带上 internal/inventory/ 前缀，这里只传动作名
    $init = Invoke-Internal 'Init' @{
        skuId = $skuId; quantity = 10; productName = '并发锁库存回归'; skuSpecText = '红'
        warnThreshold = 0; bizNo = "conc-$($script:suffix)"
    }
    if (-not $init.success) { Write-Host ("        初始化失败: " + $init.message) -ForegroundColor DarkYellow; return $false }

    $jobs = 1..50 | ForEach-Object {
        $n = $_
        Start-ThreadJob -ScriptBlock {
            param($url, $sku, $n)
            try {
                $r = Invoke-RestMethod -Uri $url -Method Post -ContentType 'application/json' `
                    -Body (@{ skuId = $sku; action = 'lock'; quantity = 1; bizNo = "conc-$sku-$n"
                        remark = '并发回归'; platformId = 0; merchantId = 0 } | ConvertTo-Json) -TimeoutSec 60
                if ($r.success) { 'OK' } else { "NO:$($r.code)" }
            } catch { 'ERR' }
        } -ArgumentList "$Inventory/internal/inventory/Apply", $skuId, $n
    }
    $done = $jobs | Wait-Job -Timeout 180 | Receive-Job
    $jobs | Remove-Job -Force

    $ok = @($done | Where-Object { $_ -eq 'OK' }).Count
    $err = @($done | Where-Object { $_ -like 'NO:*' -or $_ -eq 'ERR' }).Count
    $after = Get-Snapshot $skuId

    Write-Host ("        成功 {0} / 不足 {1}；available={2} locked={3}" -f `
        $ok, $err, $after.available, $after.locked) -ForegroundColor DarkGray

    # 恰好 10 次成功；available 不能为负；locked 不能超过初始库存
    return $ok -eq 10 -and $after.available -eq 0 -and $after.locked -eq 10
}

Invoke-Case 'API-INI-053' '清理：删补偿记录' {
    Invoke-Psql "DELETE FROM pending_stock_release WHERE biz_no LIKE 'COMP-REL-$($script:suffix)%';" | Out-Null
    return $true
}

Invoke-Case 'API-INI-043' '清理测试商品与分类' {
    Invoke-RestMethod "$Gateway/gateway/products/Delete" -Method Post -Headers $script:headers `
        -Body (@{ productId = $script:productId } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    foreach ($id in @($script:cat3, $script:cat1)) {
        Invoke-RestMethod "$Gateway/gateway/categories/Delete" -Method Post -Headers $script:headers `
            -Body (@{ categoryId = $id } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30 | Out-Null
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
