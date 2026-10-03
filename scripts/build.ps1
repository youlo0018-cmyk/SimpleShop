<#
.SYNOPSIS
    构建 .NET 解决方案。
.DESCRIPTION
    退出标准之一：0 error 且 0 warning（CODING_STANDARD 5.0 把缺注释设为编译错误）。

    Windows 上运行中的服务会锁住自己的 dll，导致 MSB3021/MSB3027 拷贝失败。
    本脚本遇到锁文件错误时会自动停掉注册表里的在跑服务并重试一次，
    省掉「先手动杀进程」这个反复踩的坑（AI_HANDOFF 已记录）。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Release,
    [switch]$NoAutoStop
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if ($Release) { $Configuration = 'Release' }

Write-Host "==> 构建 $Configuration" -ForegroundColor Cyan
Push-Location $root
try {
    $log = & dotnet build SimpleShop.slnx -c $Configuration --nologo 2>&1
    $code = $LASTEXITCODE

    # 锁文件是可恢复的失败：停掉在跑的服务再编译一次
    $locked = $code -ne 0 -and ($log | Where-Object { $_ -match 'MSB3021|MSB3027|MSB3026' })
    if ($locked -and -not $NoAutoStop) {
        Write-Host '    检测到 dll 被运行中的服务占用，自动停止服务后重试' -ForegroundColor Yellow
        & (Join-Path $PSScriptRoot 'stop-services.ps1') | Out-Null
        $log = & dotnet build SimpleShop.slnx -c $Configuration --nologo 2>&1
        $code = $LASTEXITCODE
    }

    $log | ForEach-Object { Write-Host $_ }
    if ($code -ne 0) { throw "构建失败，退出码 $code" }
}
finally {
    Pop-Location
}