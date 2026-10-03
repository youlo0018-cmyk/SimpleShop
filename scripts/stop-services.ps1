<#
.SYNOPSIS
    停止微服务。
.DESCRIPTION
    按端口找**真正占用端口的进程**来结束，而不是只信 logs/runtime/*.pid。
    原因（AI_HANDOFF 已记录）：pid 文件里往往是启动包装进程，杀它可能留下子进程继续占端口，
    下次启动就报「地址已在使用」。端口才是事实来源。
#>
[CmdletBinding()]
param(
    [string[]]$Service,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$registryPath = Join-Path $PSScriptRoot 'service-registry.json'
if (-not (Test-Path $registryPath)) { throw "找不到服务注册表: $registryPath" }

$all = (Get-Content $registryPath -Raw -Encoding UTF8 | ConvertFrom-Json).services
$targets = if ($Service) {
    @($all | Where-Object { $svc = $_; @($Service | Where-Object { $_ -and $svc.name -like "*$_*" }).Count -gt 0 })
} else { @($all) }
if ($targets.Count -eq 0) { throw "没有匹配到服务：$($Service -join ', ')" }

function Get-ListeningPids([int]$port) {
    try {
        return @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction Stop |
                 Select-Object -ExpandProperty OwningProcess -Unique)
    } catch {
        # Get-NetTCPConnection 在部分精简环境下不可用，退回 netstat 解析
        $out = netstat -ano | Select-String (":$port\s+.*LISTENING\s+(\d+)")
        return @($out | ForEach-Object { [int](($_.Line -split '\s+')[-1]) } | Sort-Object -Unique)
    }
}

foreach ($s in $targets) {
    $port = [int]$s.port

    # ---------- 无端口进程分支：只能按 pid 文件结束 ----------
    if ($port -le 0) {
        $pidFile0 = Join-Path (Join-Path $root 'logs\runtime') "$($s.name).pid"
        if (-not (Test-Path $pidFile0)) {
            Write-Host ("==> {0,-22} 无端口，且没有 pid 文件" -f $s.name) -ForegroundColor DarkGray
            continue
        }
        $raw0 = (Get-Content $pidFile0 -Raw -ErrorAction SilentlyContinue)
        $procId0 = if ([string]::IsNullOrWhiteSpace($raw0)) { 0 } else { [int]$raw0.Trim() }
        $proc0 = if ($procId0 -gt 0) { Get-Process -Id $procId0 -ErrorAction SilentlyContinue } else { $null }
        if ($null -eq $proc0) {
            Write-Host ("==> {0,-22} 无端口，进程已退出" -f $s.name) -ForegroundColor DarkGray
            continue
        }
        Write-Host ("==> 结束 {0,-22} 无端口 pid {1} ({2})" -f $s.name, $procId0, $proc0.ProcessName) -ForegroundColor Yellow
        try { Stop-Process -Id $procId0 -Force:$Force -ErrorAction Stop }
        catch { Write-Host "    结束失败: $($_.Exception.Message)" -ForegroundColor Red }
        continue
    }

    $pids = @(Get-ListeningPids $port)
    if ($pids.Count -eq 0) {
        Write-Host ("==> {0,-22} 端口 {1} 未被占用" -f $s.name, $port) -ForegroundColor DarkGray
        continue
    }

    foreach ($procId in $pids) {
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        $procName = if ($proc) { $proc.ProcessName } else { '<已退出>' }
        Write-Host ("==> 结束 {0,-22} 端口 {1} pid {2} ({3})" -f $s.name, $port, $procId, $procName) -ForegroundColor Yellow
        try { Stop-Process -Id $procId -Force:$Force -ErrorAction Stop }
        catch { Write-Host "    结束失败: $($_.Exception.Message)" -ForegroundColor Red }
    }

    # 确认端口真的释放了
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Milliseconds 400
        if (@(Get-ListeningPids $port).Count -eq 0) { break }
    }
    if (@(Get-ListeningPids $port).Count -ne 0) {
        Write-Host ("    端口 {0} 仍被占用" -f $port) -ForegroundColor Red
    }
}

Write-Host '==> 停止完成' -ForegroundColor Green