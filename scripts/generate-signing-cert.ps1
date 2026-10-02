<#
.SYNOPSIS
    生成 RS256 签名证书（.pfx）。AuthService 用私钥签发令牌，Gateway 用同一份文件验签。
.DESCRIPTION
    证书不入库（.gitignore 已忽略 *.pfx）——私钥进 git 等于把令牌签发权一起提交了。

    两边必须指向**同一个文件**（DATA_SPEC 1.5）：
    签发方要私钥，验签方只要公钥，但共用一份 pfx 最不容易搞混。
    换证书会让所有已签发令牌立刻验签失败，等于把在线用户全踢下线，
    所以默认不覆盖已存在的证书，要重签请显式加 -Force。

.PARAMETER Force
    覆盖已存在的证书文件。
.PARAMETER Path
    证书输出路径，默认 deploy/certs/signing.pfx（与 AgileConfig 里的 Auth:SigningCertificatePath 一致）。
.PARAMETER Years
    有效期年限，默认 5 年。
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Path = '',
    [string]$Password = '',
    [int]$Years = 5
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if (-not $Path) { $Path = Join-Path $root 'deploy\certs\signing.pfx' }
$full = [System.IO.Path]::GetFullPath($Path)

if ((Test-Path $full) -and -not $Force) {
    Write-Host "==> 证书已存在，未改动: $full" -ForegroundColor Yellow
    Write-Host '    换证书会让所有已签发令牌立刻验签失败。确实要重签请加 -Force。' -ForegroundColor Yellow
    exit 0
}

$dir = [System.IO.Path]::GetDirectoryName($full)
if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

Write-Host '==> 生成 RSA 3072 自签证书' -ForegroundColor Cyan

$rsa = [System.Security.Cryptography.RSA]::Create(3072)
$req = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    'CN=SimpleShop Auth Signing',
    $rsa,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

# 只用于签名与密钥交换——不做证书信任链，也不做 HTTPS 端点
$keyUsage = [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment,
    $false)
$req.CertificateExtensions.Add($keyUsage)

$eku = [System.Security.Cryptography.OidCollection]::new()
[void]$eku.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))  # serverAuth
$req.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $false))

$notBefore = [DateTimeOffset]::UtcNow.AddDays(-1)
$notAfter  = [DateTimeOffset]::UtcNow.AddYears($Years)
$cert = $req.CreateSelfSigned($notBefore, $notAfter)

$bytes = $cert.Export(
    [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
    ([string]::IsNullOrEmpty($Password) ? $null : $Password))
[System.IO.File]::WriteAllBytes($full, $bytes)
$rsa.Dispose()
$cert.Dispose()

Write-Host "    已生成: $full" -ForegroundColor Green
Write-Host "    指纹  : $((Get-PfxCertificate -FilePath $full -Password ([string]::IsNullOrEmpty($Password) ? $null : $Password) 2>$null).Thumbprint)" -ForegroundColor DarkGray
Write-Host '    AuthService 与 Gateway 的 Auth:SigningCertificatePath 必须都指向这个路径。' -ForegroundColor Cyan