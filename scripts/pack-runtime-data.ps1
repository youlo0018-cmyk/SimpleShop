<#
.SYNOPSIS
    打包运行时数据（数据卷 + 凭据 + 私钥 + 上传文件），供下一台机器直接恢复。
.DESCRIPTION
    产物：output/simpleshop-runtime-data-<时间戳>.tar.gz

    为什么必须单独打包：这些内容全部被 .gitignore 排除（数据库文件、凭据、私钥、上传件），
    git clone 拿不到；而「下一台机器直接启动」恰恰需要它们。

    内容清单：
      deploy/data/     中间件数据卷（PG 全部业务库 / Redis / AgileConfig / ES / RabbitMQ / Fluentd）
      deploy/.env      AgileConfig 管理台凭据 + 服务间内部令牌
      deploy/keys/     取货码 RSA 私钥（绝不入库）
      deploy/certs/    JWT 自签证书（如有）
      uploads/         上传的商品图 / 富文本图片
      RESTORE.md       恢复说明（追加到压缩包**根目录**，解压第一眼就能看到）

    前置条件：**中间件容器必须已停止**（scripts/stop-infra.ps1）。
    运行中的数据库打出来可能是不一致的快照，本脚本默认拒绝在容器运行时打包。

    实现细节：Windows 自带的 bsdtar 不支持 --transform / -s，所以分三步——
    先 tar 打包数据 → tar -rf 追加 RESTORE.md → .NET GZipStream 压缩
    （压缩流不能追加，必须「先追加后压缩」）。
.PARAMETER OutputDir
    产物目录，默认 output/（已被 .gitignore 排除，不会误提交）。
.PARAMETER Force
    容器在运行时也强行打包（不推荐；仅用于「数据不重要、只是想快速搬走」的场景）。
#>
[CmdletBinding()]
param(
    [string]$OutputDir = 'output',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# ① 数据一致性前置：容器在跑就不打包
$running = @(docker ps --format '{{.Names}}' 2>$null | Where-Object { $_ -like 'simpleshop-*' })
if ($running.Count -gt 0 -and -not $Force) {
    throw "以下容器仍在运行，数据目录可能不一致：$($running -join ', ')。先执行 scripts/stop-infra.ps1，或用 -Force 强行打包。"
}
if ($running.Count -gt 0) {
    Write-Warning "容器仍在运行（-Force）：$($running -join ', ')，数据一致性不保证。"
}

# ② 检查要打包的路径
$items = @('deploy/data', 'deploy/.env', 'deploy/keys', 'deploy/certs', 'uploads')
foreach ($m in $items | Where-Object { -not (Test-Path (Join-Path $root $_)) }) {
    Write-Warning "跳过不存在的路径：$m"
}
$present = @($items | Where-Object { Test-Path (Join-Path $root $_) })
if ($present.Count -eq 0) { throw '没有任何可打包的运行时数据。' }

$restore = Join-Path $root 'deploy/linux/RESTORE.md'
if (-not (Test-Path $restore)) { throw "缺少恢复说明：$restore" }

# ③ 产物路径
$outDir = Join-Path $root $OutputDir
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$tmpTar = Join-Path $outDir "simpleshop-runtime-data-$stamp.tar"
$final = "$tmpTar.gz"

# ④ 打包 → 追加说明 → 压缩
Write-Host '==> 打包数据（tar）' -ForegroundColor Cyan
& tar -cf $tmpTar -C $root @present
if ($LASTEXITCODE -ne 0) { throw "tar 打包失败，退出码 $LASTEXITCODE" }

Write-Host '==> 把 RESTORE.md 追加到压缩包根目录' -ForegroundColor Cyan
& tar -rf $tmpTar -C (Join-Path $root 'deploy/linux') 'RESTORE.md'
if ($LASTEXITCODE -ne 0) { throw "tar 追加失败，退出码 $LASTEXITCODE" }

Write-Host '==> 压缩（gzip）' -ForegroundColor Cyan
$in = [IO.File]::OpenRead($tmpTar)
try {
    $out = [IO.File]::Create($final)
    try {
        $gz = [IO.Compression.GZipStream]::new($out, [IO.Compression.CompressionLevel]::Optimal)
        try { $in.CopyTo($gz) } finally { $gz.Dispose() }
    } finally { $out.Dispose() }
} finally { $in.Dispose() }
Remove-Item -LiteralPath $tmpTar -Force

$size = [math]::Round((Get-Item $final).Length / 1MB, 1)
Write-Host "==> 完成：$final（$size MB）" -ForegroundColor Green
Write-Host '    下一台机器：解压到仓库根目录 → 按 deploy/linux/README.md 启动' -ForegroundColor DarkGray
