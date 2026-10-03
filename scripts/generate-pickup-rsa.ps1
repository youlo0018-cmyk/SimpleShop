<#
.SYNOPSIS
    生成自提取货码用的 RSA 密钥对（幂等可重跑）。

.DESCRIPTION
    取货码 = 公钥加密后的订单号（BUSINESS.md 8.3 / 用户需求 D6）。
    公私钥都只在服务端：前端只显示与回传密文，加解密全部由服务端完成。

    重新生成会让**所有已发出的取货码失效**——之前备好货、还没来取的顾客会核销不了。
    所以默认不会覆盖已有密钥，必须显式加 -Force。
#>
[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\deploy\keys'),
    [int]$KeySize = 2048,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$privatePath = Join-Path $OutputDir 'pickup-code-private.pem'
$publicPath  = Join-Path $OutputDir 'pickup-code-public.pem'

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    Write-Host "已创建目录 $OutputDir" -ForegroundColor Cyan
}

if ((Test-Path $privatePath) -and -not $Force) {
    Write-Host "私钥已存在：$privatePath" -ForegroundColor DarkGray
    Write-Host "重新生成会让所有已发出的取货码失效。确实要换请加 -Force。" -ForegroundColor Yellow
    return
}

$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.KeySize = $KeySize

    # 私钥写 PKCS#8（BEGIN PRIVATE KEY）。RSA.ImportFromPem 能直接读这个格式。
    $privatePem = $rsa.ExportPkcs8PrivateKeyPem()
    [System.IO.File]::WriteAllText($privatePath, $privatePem, [System.Text.UTF8Encoding]::new($false))

    $publicPem = $rsa.ExportSubjectPublicKeyInfoPem()
    [System.IO.File]::WriteAllText($publicPath, $publicPem, [System.Text.UTF8Encoding]::new($false))
}
finally {
    $rsa.Dispose()
}

Write-Host "==> 私钥：$privatePath" -ForegroundColor Green
Write-Host "==> 公钥：$publicPath" -ForegroundColor Green
Write-Host ""
Write-Host "私钥不要提交到仓库、不要打进发布包。.gitignore 里已忽略 deploy/keys/。" -ForegroundColor Yellow