<#
.SYNOPSIS
    跑全部前端回归：静态核对 → 后台页面巡检 → 后台深度回归 → 小程序巡检。

.DESCRIPTION
    顺序不是随意的：
      ① `check-route-links.mjs` 是静态的（秒级），先跑 —— 链接指向不存在的路由时，
         后面的界面回归只会看到「点了没反应 / 回到工作台」，很难定位；
      ② `admin-ui-regression.mjs` 覆盖 31 个菜单页 + 27 个非菜单页 + 10 条流程；
      ③ `admin-deep-regression.mjs` 覆盖页内控件（页签 / 筛选 / 分页 / 行操作 / 表单 / 树 / 装修…）；
      ④ `user-ui-regression.mjs` 覆盖小程序 22 页。

    每个脚本的「通过 / 失败 / 退出码」收成一行，最后统一给退出码。
    需要先起服务与两个前端预览（scripts/start-services.ps1 + scripts/start-web.ps1）。
#>
[CmdletBinding()]
param(
    [string]$Filter = ''
)

$ErrorActionPreference = 'Continue'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')

$steps = @(
    @{ Name = 'check-route-links'; Cmd = 'node'; Args = @('tests/ui/check-route-links.mjs') },
    @{ Name = 'admin-ui-regression'; Cmd = 'node'; Args = @('tests/ui/admin-ui-regression.mjs') },
    @{ Name = 'admin-deep-regression'; Cmd = 'node'; Args = @('tests/ui/admin-deep-regression.mjs') },
    @{ Name = 'user-ui-regression'; Cmd = 'node'; Args = @('tests/ui/user-ui-regression.mjs') }
)

if ($Filter) { $steps = $steps | Where-Object { $_.Name -like "*$Filter*" } }

$rows = @()
$failed = @()

foreach ($step in $steps) {
    Write-Host ''
    Write-Host ('#' * 70) -ForegroundColor DarkGray
    Write-Host ("# 运行 " + $step.Name) -ForegroundColor Cyan
    Write-Host ('#' * 70) -ForegroundColor DarkGray

    Push-Location $root
    try {
        # 6>&1：子脚本的 Write-Host 走信息流（stream 6），不重定向的话收不进变量
        $output = (& $step.Cmd @($step.Args) 6>&1 2>&1 | Out-String)
        $code = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    Write-Host $output
    $rows += [pscustomobject]@{ Name = $step.Name; ExitCode = $code }
    if ($code -ne 0) { $failed += $step.Name }
}

Write-Host ''
Write-Host ('=' * 70) -ForegroundColor DarkGray
Write-Host ' 前端回归汇总' -ForegroundColor Cyan
Write-Host ('=' * 70) -ForegroundColor DarkGray
foreach ($r in $rows) {
    $color = if ($r.ExitCode -eq 0) { 'Green' } else { 'Red' }
    Write-Host ("  {0,-26} 退出码 {1}" -f $r.Name, $r.ExitCode) -ForegroundColor $color
}

if ($failed.Count -gt 0) {
    Write-Host ("`n失败：" + ($failed -join '、')) -ForegroundColor Red
    exit 1
}

Write-Host "`n全部通过" -ForegroundColor Green
