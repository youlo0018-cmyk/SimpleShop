<#
.SYNOPSIS
    跑全部端到端回归脚本，输出一张汇总表。

.DESCRIPTION
    单个脚本全量打印，用管道筛很容易漏掉失败行；这里把每个脚本的
    「通过 / 失败 / 退出码」收成一行，最后统一给退出码。
    任何一个脚本失败整体退出码非 0，方便挂到 CI 上。

    注意：脚本本身会向控制台打印明细（Write-Host 绕不过去），
    汇总表打在最后，CI 里看最后一段即可。

    这里只收 `*-regression.ps1`。**`seckill-concurrency.ps1` 有意不纳入**：
    它要起 200 个 `Start-ThreadJob` 线程，塞进日常回归会把 2 秒的脚本拖成一分钟。
    真正验收并发防超卖时单独跑它（PLAN.md S7 验收步骤 1、2）。
#>
[CmdletBinding()]
param(
    [string]$Filter = '',
    [switch]$StopOnFirstFail
)

$ErrorActionPreference = 'Continue'

# 先跑静态核对：权限点绑的 api_path 是否真的对得上某个后端端点。
# 放在最前面是因为它**不需要起服务**（秒级），而且它是唯一能抓到
# 「api_path 写错 = 接口彻底不鉴权」这一类缺陷的手段 ——
# 那种缺陷对任何账号都返回 200，用超管测一万遍也测不出来。
Write-Host ("#" * 70) -ForegroundColor DarkGray
Write-Host "# 运行 check-permission-paths.ps1（静态核对权限路径）" -ForegroundColor Cyan
Write-Host ("#" * 70) -ForegroundColor DarkGray

$permCheck = Join-Path $PSScriptRoot '..\..\scripts\check-permission-paths.ps1'
$permOutput = (& $permCheck 6>&1 2>&1 | Out-String)
$permExit = $LASTEXITCODE
Write-Host $permOutput

if ($permExit -ne 0) {
    Write-Host "权限路径核对未通过，后续端到端回归不再执行（否则测的是一套不鉴权的接口）" -ForegroundColor Red
    exit 1
}

# ---------- 服务存活预检 ----------
#
# 为什么必须有这一步：服务没起时，脚本里第一句 `Invoke-RestMethod` 会**卡在连接超时上**
# （不是立刻报错），整套回归会「静默挂十几分钟」，看起来像脚本写死了。
# 实测踩过：`build.ps1` 检测到 dll 被占用时会自动 `stop-services.ps1` 再重编
# （见 build.ps1 的说明），于是「构建成功 → 直接跑回归」必然是一整套服务全没起。
# 这里先探一遍 /health，缺谁就把名字列出来并直接退出，把「挂住」变成「一眼看明白」。
$registryPath = Join-Path $PSScriptRoot '..\..\scripts\service-registry.json'
$services = (Get-Content $registryPath -Raw -Encoding UTF8 | ConvertFrom-Json).services |
    Where-Object { $_.implemented -and $_.port }

$unhealthy = @()
foreach ($svc in $services) {
    try {
        $probe = Invoke-WebRequest "http://127.0.0.1:$($svc.port)/health" -UseBasicParsing -TimeoutSec 3
        if ($probe.StatusCode -ne 200) { $unhealthy += "$($svc.name)($($svc.port))" }
    }
    catch {
        $unhealthy += "$($svc.name)($($svc.port))"
    }
}

if ($unhealthy.Count -gt 0) {
    Write-Host ("服务未就绪：" + ($unhealthy -join '、')) -ForegroundColor Red
    Write-Host '请先运行 ./scripts/start-services.ps1（构建脚本在 dll 被占用时会自动停掉全部服务，构建完必须重新启动）' -ForegroundColor Yellow
    exit 1
}

Write-Host ("服务存活预检通过（" + $services.Count + " 个）") -ForegroundColor Green

$scripts = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*-regression.ps1' | Sort-Object Name
if ($Filter) { $scripts = $scripts | Where-Object { $_.Name -like "*$Filter*" } }

$rows = @()
$failed = @()

foreach ($s in $scripts) {
    Write-Host ""
    Write-Host ("#" * 70) -ForegroundColor DarkGray
    Write-Host ("# 运行 " + $s.Name) -ForegroundColor Cyan
    Write-Host ("#" * 70) -ForegroundColor DarkGray

    # 6>&1 是必须的：子脚本用 Write-Host 打结果，而 Write-Host 走的是**信息流**（stream 6），
    # 不是标准输出。不接住它的话 $output 永远是空串，汇总表全是 0——
    # 一张全 0 的表比没有表更糟，它让人以为「没跑」。
    $output = (& $s.FullName 6>&1 2>&1 | Out-String)
    $code = $LASTEXITCODE

    # 取最后一段「通过: N 失败: M」。有的脚本会打印多段汇总，取最后一次。
    $matches = [regex]::Matches($output, '通过:\s*(\d+)\s+失败:\s*(\d+)')
    $pass = if ($matches.Count -gt 0) { [int]$matches[$matches.Count - 1].Groups[1].Value } else { 0 }
    $fail = if ($matches.Count -gt 0) { [int]$matches[$matches.Count - 1].Groups[2].Value } else { -1 }

    # 失败明细也要留：只看总数不知道是哪条坏了
    $failNames = @()
    $fm = [regex]::Match($output, '(?s)失败用例:\s*(.*)$')
    if ($fm.Success) {
        $failNames = @($fm.Groups[1].Value -split "`n" |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -match '^-\s' } |
            ForEach-Object { $_.TrimStart('-', ' ') })
    }

    $rows += [pscustomobject]@{
        Script  = $s.Name
        Pass    = $pass
        Fail    = $fail
        Exit    = $code
        Details = ($failNames -join '; ')
    }

    if ($code -ne 0) {
        $failed += $s.Name
        if ($StopOnFirstFail) { break }
    }
}

Write-Host ""
Write-Host ("=" * 70) -ForegroundColor Cyan
Write-Host " 端到端回归汇总" -ForegroundColor Cyan
Write-Host ("=" * 70) -ForegroundColor Cyan

foreach ($r in $rows) {
    $color = if ($r.Exit -eq 0) { 'Green' } else { 'Red' }
    Write-Host ("  {0,-30} 通过 {1,4}  失败 {2,4}  退出码 {3}" -f $r.Script, $r.Pass, $r.Fail, $r.Exit) -ForegroundColor $color
    if ($r.Details) { Write-Host ("      " + $r.Details) -ForegroundColor Red }
}

$totalPass = ($rows | Measure-Object -Property Pass -Sum).Sum
$totalFail = ($rows | Measure-Object -Property Fail -Sum).Sum
Write-Host ("-" * 70) -ForegroundColor Cyan
Write-Host ("  合计：通过 {0}，失败 {1}，脚本 {2} 个" -f $totalPass, $totalFail, $rows.Count) -ForegroundColor Cyan

if ($failed.Count -gt 0) {
    Write-Host ("  失败脚本：" + ($failed -join ', ')) -ForegroundColor Red
    exit 1
}

Write-Host "  全部通过" -ForegroundColor Green
exit 0
