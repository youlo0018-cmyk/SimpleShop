<#
.SYNOPSIS
    启动 SimpleShop 本地中间件并等待健康检查通过。
.DESCRIPTION
    依据 PLAN.md 2.3。默认启动 7 个容器；AgileConfig 在 config profile 中，
    官方 registry 当前不可达时不会启动（见 docker-compose.yml 内的说明）。
#>
[CmdletBinding()]
param(
    [switch]$WithConfig,
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$deployDir = Join-Path $PSScriptRoot '..\deploy'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw '未找到 docker，请先安装 Docker Desktop 并启动。'
}

Push-Location $deployDir
try {
    $composeArgs = @('compose', 'up', '-d')
    if ($WithConfig) { $composeArgs += '--profile'; $composeArgs += 'config' }
    Write-Host '==> 启动中间件' -ForegroundColor Cyan
    & docker @composeArgs
    if ($LASTEXITCODE -ne 0) { throw "docker compose up 失败，退出码 $LASTEXITCODE" }

    Write-Host '==> 等待健康检查' -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $expected = @(
        'simpleshop-postgres', 'simpleshop-redis', 'simpleshop-consul',
        'simpleshop-rabbitmq', 'simpleshop-elasticsearch', 'simpleshop-kibana',
        'simpleshop-fluentd'
    )
    if ($WithConfig) { $expected += 'simpleshop-agileconfig' }

    while ((Get-Date) -lt $deadline) {
        $pending = @()
        foreach ($name in $expected) {
            $state = docker inspect --format '{{.State.Health.Status}}' $name 2>$null
            if ($state -ne 'healthy') { $pending += "$name($state)" }
        }
        if ($pending.Count -eq 0) {
            Write-Host "==> 全部 $($expected.Count) 个容器已 healthy" -ForegroundColor Green
            return
        }
        Write-Host ("    等待中: " + ($pending -join ', '))
        Start-Sleep -Seconds 5
    }
    throw "等待超时，仍未 healthy: $($pending -join ', ')"
}
finally {
    Pop-Location
}

