<#
.SYNOPSIS
    执行各服务的建表脚本，幂等可重跑。
.DESCRIPTION
    依据 DATA_SPEC.md 2.9：表结构由 deploy/sql/<service>/ 下的 SQL 脚本管理，不使用 CodeFirst。
    目录名去掉 simpleshop 前缀后即库名，例如 deploy/sql/customer -> simpleshopcustomer。
#>
[CmdletBinding()]
param(
    [string]$Container = 'simpleshop-postgres',
    [string]$User = 'postgres',
    [string[]]$Service
)

$ErrorActionPreference = 'Stop'
$sqlRoot = Join-Path $PSScriptRoot '..\deploy\sql'

if (-not (Test-Path $sqlRoot)) { throw "找不到 SQL 目录: $sqlRoot" }

$dirs = if ($Service) { $Service } else { (Get-ChildItem -LiteralPath $sqlRoot -Directory).Name }

foreach ($svc in $dirs) {
    $dir = Join-Path $sqlRoot $svc
    if (-not (Test-Path $dir)) { throw "服务目录不存在: $dir" }

    $files = Get-ChildItem -LiteralPath $dir -Filter '*.sql' | Sort-Object Name
    if ($files.Count -eq 0) { Write-Host "跳过 $svc（无 SQL 文件）" -ForegroundColor DarkGray; continue }

    $db = "simpleshop$svc"
    foreach ($f in $files) {
        Write-Host "==> [$db] 执行 $($f.Name)" -ForegroundColor Cyan
        Get-Content -LiteralPath $f.FullName -Raw |
            docker exec -i $Container psql -U $User -d $db -v ON_ERROR_STOP=1 2>&1 |
            ForEach-Object { if ($_ -and $_ -notmatch '^(CREATE|ALTER|COMMENT|INSERT|UPDATE|DELETE|SELECT|DO|NOTICE|\s*$)') { Write-Host "    $_" -ForegroundColor Red } }

        if ($LASTEXITCODE -ne 0) { throw "[$db] $($f.Name) 执行失败，退出码 $LASTEXITCODE" }
    }
    Write-Host "    [$db] 完成" -ForegroundColor Green
}

