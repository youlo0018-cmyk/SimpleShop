<#
.SYNOPSIS
    后台启动微服务（多实例并行，日志与 PID 落在 logs/runtime/）。
.DESCRIPTION
    服务清单来自 scripts/service-registry.json——端口表只维护这一份。
    implemented=false 的服务会被跳过，不会因为「还没写」而让整脚本失败。
    用已编译好的 dll 启动而不是 dotnet run：启动更快，也不会因为编译产物过期而行为诡异。
    启动前先调 /health 探活，已经在跑的服务不会被重复拉起。
.PARAMETER Service
    只启动指定服务（支持部分名匹配，例如 -Service Tool 会命中 ToolService）。
#>
[CmdletBinding()]
param(
    [string[]]$Service,
    [switch]$Recompile
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$registryPath = Join-Path $PSScriptRoot 'service-registry.json'
if (-not (Test-Path $registryPath)) { throw "找不到服务注册表: $registryPath" }

$all = (Get-Content $registryPath -Raw -Encoding UTF8 | ConvertFrom-Json).services
$targets = $all | Where-Object { $_.implemented }

if ($Service) {
    # 部分名匹配：-Service Tool 命中 ToolService，-Service Service 命中全部。
    $matched = @($targets | Where-Object {
        $svc = $_
        @($Service | Where-Object { $_ -and $svc.name -like "*$_*" }).Count -gt 0
    })
    if ($matched.Count -eq 0) { throw "没有匹配到已实现的服务：$($Service -join ', ')" }
    $targets = $matched
}

$logDir = Join-Path $root 'logs\runtime'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

if ($Recompile) {
    Write-Host '==> 先构建' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw '构建失败，放弃启动。' }
}

function Test-Healthy([int]$port) {
    try {
        $r = Invoke-WebRequest "http://127.0.0.1:$port/health" -TimeoutSec 2 -UseBasicParsing
        return $r.StatusCode -eq 200
    } catch { return $false }
}

# 无端口进程（定时任务）只能用 pid 文件判断存活：
# 它们不监听端口，也没有 /health 可探活，端口表对它们是空的。
function Test-AliveFromPidFile([string]$pidFile) {
    if (-not (Test-Path $pidFile)) { return $false }
    $raw = (Get-Content $pidFile -Raw -ErrorAction SilentlyContinue)
    if ([string]::IsNullOrWhiteSpace($raw)) { return $false }
    $proc = Get-Process -Id ([int]$raw.Trim()) -ErrorAction SilentlyContinue
    return ($null -ne $proc)
}

$started = 0
foreach ($s in $targets) {
    $port = [int]$s.port

    # ---------- 无端口进程分支 ----------
    if ($port -le 0) {
        $logDir0 = $root
        $proj0 = Join-Path $logDir0 $s.project
        if (-not (Test-Path $proj0)) { throw "项目不存在: $proj0" }
        $projDir0 = Split-Path -Parent $proj0
        $asm0 = [IO.Path]::GetFileNameWithoutExtension($proj0)
        $dll0 = Join-Path $projDir0 "bin\Debug\net10.0\$asm0.dll"
        if (-not (Test-Path $dll0)) { throw "找不到编译产物 $dll0，请先运行 ./scripts/build.ps1" }

        $log0 = Join-Path (Join-Path $root 'logs\runtime') "$($s.name).log"
        $err0 = Join-Path (Join-Path $root 'logs\runtime') "$($s.name).err.log"
        $pidFile0 = Join-Path (Join-Path $root 'logs\runtime') "$($s.name).pid"

        if (Test-AliveFromPidFile $pidFile0) {
            Write-Host ("==> {0,-22} 无端口，已在运行，跳过" -f $s.name) -ForegroundColor DarkGray
            continue
        }

        Write-Host ("==> 启动 {0,-22}（无端口后台进程）" -f $s.name) -ForegroundColor Cyan
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $proc0 = Start-Process -FilePath 'dotnet' `
            -ArgumentList @($dll0) `
            -WorkingDirectory $projDir0 `
            -RedirectStandardOutput $log0 -RedirectStandardError $err0 `
            -WindowStyle Hidden -PassThru
        [System.IO.File]::WriteAllText($pidFile0, [string]$proc0.Id)

        # 没有 /health 可探活，只能看「启动后几秒内有没有立刻崩」
        Start-Sleep -Seconds 4
        if ($proc0.HasExited) {
            $tail0 = if (Test-Path $err0) { (Get-Content $err0 -Tail 5) -join ' | ' } else { '(无日志)' }
            Write-Host ("    启动失败 (pid {0} 退出码 {1})" -f $proc0.Id, $proc0.ExitCode) -ForegroundColor Red
            Write-Host "    错误日志: $tail0" -ForegroundColor Red
        } else {
            Write-Host ("    已启动 (pid {0})" -f $proc0.Id) -ForegroundColor Green
            $started++
        }
        continue
    }

    if (Test-Healthy $port) {
        Write-Host ("==> {0,-22} 端口 {1} 已在运行，跳过" -f $s.name, $port) -ForegroundColor DarkGray
        continue
    }

    $proj = Join-Path $root $s.project
    if (-not (Test-Path $proj)) { throw "项目不存在: $proj（service-registry.json 的 implemented 标记与实际不符）" }

    # csproj 同名 dll：<项目目录>/bin/<配置>/<TFM>/<项目名>.dll
    $projDir = Split-Path -Parent $proj
    $assembly = [IO.Path]::GetFileNameWithoutExtension($proj)
    $dll = Join-Path $projDir "bin\Debug\net10.0\$assembly.dll"
    if (-not (Test-Path $dll)) {
        throw "找不到编译产物 $dll，请先运行 ./scripts/build.ps1"
    }

    $log = Join-Path $logDir "$($s.name).log"
    $err = Join-Path $logDir "$($s.name).err.log"
    $pidFile = Join-Path $logDir "$($s.name).pid"

    Write-Host ("==> 启动 {0,-22} http://127.0.0.1:{1}" -f $s.name, $port) -ForegroundColor Cyan
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $proc = Start-Process -FilePath 'dotnet' `
        -ArgumentList @($dll, '--urls', "http://127.0.0.1:$port") `
        -WorkingDirectory $projDir `
        -RedirectStandardOutput $log -RedirectStandardError $err `
        -WindowStyle Hidden -PassThru

    [System.IO.File]::WriteAllText($pidFile, [string]$proc.Id)

    # 最多等 30 秒 /health 就绪
    $ok = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 1000
        if ($proc.HasExited) { break }
        if (Test-Healthy $port) { $ok = $true; break }
    }

    if ($ok) {
        Write-Host ("    就绪 (pid {0})" -f $proc.Id) -ForegroundColor Green
        $started++
    } else {
        $tail = if (Test-Path $log) { (Get-Content $log -Tail 5) -join ' | ' } else { '(无日志)' }
        Write-Host ("    启动失败或超时 (pid {0} exited={1})" -f $proc.Id, $proc.HasExited) -ForegroundColor Red
        Write-Host "    日志尾部: $tail" -ForegroundColor Red
    }
}

Write-Host "==> 启动完成，成功 $started 个" -ForegroundColor Green