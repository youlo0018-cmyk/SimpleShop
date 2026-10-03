<#
.SYNOPSIS
    跑全部端到端回归脚本，输出一张汇总表。

.DESCRIPTION
    单个脚本全量打印，用管道筛很容易漏掉失败行；这里把每个脚本的
    「通过 / 失败 / 退出码」收成一行，最后统一给退出码。
    任何一个脚本失败整体退出码非 0，方便挂到 CI 上。

    注意：脚本本身会向控制台打印明细（Write-Host 绕不过去），
    汇总表打在最后，CI 里看最后一段即可。
#>
[CmdletBinding()]
param(
    [string]$Filter = '',
    [switch]$StopOnFirstFail
)

$ErrorActionPreference = 'Continue'

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