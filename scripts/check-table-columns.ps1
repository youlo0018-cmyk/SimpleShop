<#
.SYNOPSIS
    扫描所有业务库，找出「实体基类有、建表脚本没有」的列。
.DESCRIPTION
    为什么需要这个脚本：FreeSql 的实体映射来自 C# 类，**不看建表脚本**。
    实体继承了 AdminEntityBase，建表时漏掉 created_by_id，
    编译期完全正常、启动也正常，直到第一次读写那张表才报
    `42703: column a.created_by_id does not exist`（HTTP 500）。
    真实踩过：seckill_grab 就是这么漏的，秒杀抢购整条链路 500。

    判定方式：先解析 C# 源码拿到「表名 → 基类名」映射，再按基类要求核对实际列。
    只靠「表里有没有 platform_id」猜是不准的——Order : EntityBase 也有 platform_id，
    但它的操作人语义是客户而不是后台账号，硬加 4 列反而是错的。
.PARAMETER Container
    PostgreSQL 容器名。
.PARAMETER SkipDatabase
    排除的库（AgileConfig 自己的库不是本项目实体，不参与检查）。
#>
[CmdletBinding()]
param(
    [string]$Container = 'simpleshop-postgres',
    [string[]]$SkipDatabase = @('simpleshop_configcenter'),
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

# ---- 1. 解析实体：表名 → 基类 ----
$tableToBase = @{}

foreach ($file in Get-ChildItem "$RepoRoot\src" -Recurse -Filter '*.cs') {
    if ($file.FullName -match '[\\/](bin|obj)[\\/]') { continue }

    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        # [Table(Name = "xxx")] 后面紧跟的就是实体类声明。
        # 引号用 "+ 量词：order 表在 C# 里写成原始字符串字面量 """order"""（order 是 PG 保留字），
        # 只匹配单个引号会漏掉它。
        if ($lines[$i] -match '^\s*\[Table\(\s*Name\s*=\s*"+([^"]+)"+\s*\)\]\s*$') {
            $table = $Matches[1]

            for ($j = $i + 1; $j -lt [Math]::Min($i + 4, $lines.Length); $j++) {
                if ($lines[$j] -match '^\s*(?:public|internal)?\s*(?:sealed\s+)?class\s+\w+(?:\s*<[^>]*>)?\s*(?::\s*([\w<>]+))?') {
                    $base = $Matches[1]
                    if (-not $base) { $base = '(无)' }
                    $tableToBase[$table] = $base
                    break
                }
            }
        }
    }
}

Write-Host "解析到 $($tableToBase.Count) 个实体"

# ---- 2. 逐库核对列 ----
$databases = @(
    docker exec $Container psql -U postgres -t -A -c `
        "SELECT datname FROM pg_database WHERE datistemplate = false AND datname LIKE 'simpleshop%'"
) | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notin $SkipDatabase }

$query = @"
SELECT table_name, string_agg(column_name, ',')
FROM information_schema.columns
WHERE table_schema = 'public'
GROUP BY table_name
ORDER BY table_name;
"@

$total = 0
$checked = 0
$bad = @()

foreach ($db in $databases) {
    $rows = docker exec $Container psql -U postgres -d $db -t -A -F '|' -c $query

    foreach ($row in $rows) {
        if (-not $row -or -not $row.Trim()) { continue }
        $total++

        $parts = $row -split '\|'
        $table = $parts[0].Trim()

        # 建了表但源码里没有对应实体 → 只提示，不判定（可能是手工建的辅助表）
        if (-not $tableToBase.ContainsKey($table)) { continue }

        $base = $tableToBase[$table]
        $cols = @($parts[1] -split ',' | ForEach-Object { $_.Trim() })
        $checked++

        $missing = @()

        if ($base -eq 'EntityBase' -or $base -eq 'AdminEntityBase' -or $base -eq 'CustomerEntityBase') {
            foreach ($c in 'id', 'created_at', 'updated_at', 'is_deleted', 'deleted_at') {
                if ($cols -notcontains $c) { $missing += $c }
            }
        }

        if ($base -eq 'AdminEntityBase') {
            foreach ($c in 'created_by_id', 'created_by_name', 'operation_id', 'operation_name', 'platform_id', 'merchant_id') {
                if ($cols -notcontains $c) { $missing += $c }
            }
        }

        if ($base -eq 'CustomerEntityBase' -and $cols -notcontains 'customer_id') {
            $missing += 'customer_id'
        }

        if ($missing.Count) {
            $bad += [pscustomobject]@{
                Database = $db
                Table    = $table
                Base     = $base
                Missing  = $missing -join ', '
            }
        }
    }
}

Write-Host "扫描 $total 张表，其中 $checked 张能对应到实体"

if ($bad.Count -eq 0) {
    Write-Host "  所有实体的基类列齐全" -ForegroundColor Green
    exit 0
}

foreach ($item in $bad) {
    Write-Host ("  {0}.{1}  基类 {2}  缺: {3}" -f $item.Database, $item.Table, $item.Base, $item.Missing) -ForegroundColor Red
}
Write-Host "  $($bad.Count) 张表有缺列：补建表脚本后重跑 ./scripts/init-tables.ps1 -Service <服务>" -ForegroundColor Red
exit 1
