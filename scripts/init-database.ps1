<#
.SYNOPSIS
    执行 deploy/sql 下的建库脚本，幂等可重跑。
.DESCRIPTION
    依据 DATA_SPEC.md 2.9：建表与初始化一律走 SQL 脚本，不使用 CodeFirst。
#>
[CmdletBinding()]
param(
    [string]$Container = 'simpleshop-postgres',
    [string]$User = 'postgres'
)

$ErrorActionPreference = 'Stop'
$sqlDir = Join-Path $PSScriptRoot '..\deploy\sql'
$initFile = Join-Path $sqlDir '00-create-databases.sql'

if (-not (Test-Path $initFile)) { throw "找不到建库脚本: $initFile" }

Write-Host '==> 确认 postgres 已就绪' -ForegroundColor Cyan
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    docker exec $Container pg_isready -U $User 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $ready = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $ready) { throw "postgres 容器未就绪: $Container" }

Write-Host '==> 执行 00-create-databases.sql' -ForegroundColor Cyan
Get-Content -LiteralPath $initFile -Raw |
    docker exec -i $Container psql -U $User -d postgres -v ON_ERROR_STOP=1

if ($LASTEXITCODE -ne 0) { throw "建库脚本执行失败，退出码 $LASTEXITCODE" }

Write-Host '==> 完成' -ForegroundColor Green

