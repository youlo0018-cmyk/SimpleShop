<#
.SYNOPSIS
    停止 SimpleShop 本地中间件。
    默认保留数据卷；传 -PurgeData 一并删除。
#>
[CmdletBinding()]
param(
    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'
$deployDir = Join-Path $PSScriptRoot '..\deploy'

Push-Location $deployDir
try {
    $args = @('compose', '--profile', 'config', 'down')
    if ($PurgeData) { $args += '-v' }
    & docker @args
}
finally {
    Pop-Location
}

