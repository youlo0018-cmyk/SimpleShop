<#
.SYNOPSIS
    构建 .NET 解决方案。
    退出标准之一：0 error 且 0 warning（CODING_STANDARD 5.0 把缺注释设为编译错误）。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if ($Release) { $Configuration = 'Release' }

Write-Host "==> 构建 $Configuration" -ForegroundColor Cyan
Push-Location $root
try {
    & dotnet build SimpleShop.slnx -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "构建失败，退出码 $LASTEXITCODE" }
}
finally {
    Pop-Location
}

