<#
.SYNOPSIS
    统一文件上传的回归（TEST_CASES API-UP-001 / REVIEW 链路 6.5）。
.DESCRIPTION
    之前**一条上传用例都没有**，而这三步校验全是安全边界：
    扩展名白名单 → 分类大小上限 → 文件头魔数。
    魔数那一层专门防「把任意脚本改名成 .png 传上来」，
    它一旦被改坏，没有任何测试会红 —— 后台所有图片上传从此形同虚设。

    业务失败一律是 HTTP 200 + success=false（FluentValidation 才 400），
    所以断言 success 而不是状态码。
#>
[CmdletBinding()]
param(
    [string]$Gateway = 'http://127.0.0.1:5008',
    [string]$AdminUser = 'codexadmin',
    [string]$AdminPassword = 'Admin123456',
    [switch]$StopOnFail
)

$ErrorActionPreference = 'Continue'
$script:pass = 0
$script:fail = 0
$script:failures = @()

function Invoke-Case {
    param([string]$Id, [string]$Name, [scriptblock]$Action)
    try {
        if (& $Action) {
            $script:pass++
            Write-Host ("  PASS  " + $Id + "  " + $Name) -ForegroundColor Green
        } else {
            $script:fail++
            $script:failures += "$Id $Name"
            Write-Host ("  FAIL  " + $Id + "  " + $Name) -ForegroundColor Red
            if ($StopOnFail) { throw "用例 $Id 失败，已按 -StopOnFail 中止" }
        }
    } catch {
        $script:fail++
        $script:failures += "$Id $Name (异常: $($_.Exception.Message))"
        Write-Host ("  FAIL  " + $Id + "  " + $Name + "  " + $_.Exception.Message) -ForegroundColor Red
        if ($StopOnFail) { throw }
    }
}

# ---------- 令牌 ----------
$http = [System.Net.Http.HttpClient]::new()
$form = [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string, string]]]::new()
$form.Add([System.Collections.Generic.KeyValuePair[string, string]]::new('grant_type', 'password'))
$form.Add([System.Collections.Generic.KeyValuePair[string, string]]::new('client_id', 'admin-app'))
$form.Add([System.Collections.Generic.KeyValuePair[string, string]]::new('username', $AdminUser))
$form.Add([System.Collections.Generic.KeyValuePair[string, string]]::new('password', $AdminPassword))
$tokenResp = $http.PostAsync("$Gateway/gateway/auth/token",
    [System.Net.Http.FormUrlEncodedContent]::new($form)).GetAwaiter().GetResult()
$token = ($tokenResp.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).access_token
$script:adminHeaders = @{ Authorization = "Bearer $token" }

# ---------- 上传辅助 ----------
$script:tmpDir = Join-Path $env:TEMP "simpleshop-upload-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $script:tmpDir | Out-Null

function New-UploadFile([string]$FileName, [byte[]]$Bytes) {
    $path = Join-Path $script:tmpDir $FileName
    [System.IO.File]::WriteAllBytes($path, $Bytes)
    return $path
}

function Invoke-Upload([string]$Path) {
    $multi = [System.Net.Http.MultipartFormDataContent]::new()
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $multi.Add([System.Net.Http.ByteArrayContent]::new($bytes), 'file', [System.IO.Path]::GetFileName($Path))
    $req = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post, "$Gateway/gateway/files/Upload")
    $req.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
    $req.Content = $multi
    $resp = $http.SendAsync($req).GetAwaiter().GetResult()
    return ($resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json)
}

# 1×1 透明 PNG 的真实字节
$realPng = [Convert]::FromBase64String(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==')

Write-Host "`n=== UP 上传：扩展名白名单 / 大小上限 / 魔数 ===" -ForegroundColor Cyan

Invoke-Case 'API-UP-000' '对照组：真 PNG 上传成功' {
    $r = Invoke-Upload (New-UploadFile 'ok.png' $realPng)
    if ($r.success) { $script:uploadedUrl = $r.data.publicUrl }
    return $r.success -and $r.data.extension -eq 'png' -and $r.data.category -eq 'image'
}

Invoke-Case 'API-UP-001' '🔴 P0 伪造扩展名被拒（内容其实是纯文本）' {
    $text = [System.Text.Encoding]::UTF8.GetBytes('this is plain text pretending to be a png')
    $r = Invoke-Upload (New-UploadFile 'fake.png' $text)
    return (-not $r.success) -and $r.message -match '不匹配'
}

Invoke-Case 'API-UP-002' '🔴 P0 白名单外的扩展名被拒（.exe）' {
    $r = Invoke-Upload (New-UploadFile 'evil.exe' ([System.Text.Encoding]::UTF8.GetBytes('MZ')))
    return (-not $r.success) -and $r.message -match '不支持|白名单|格式'
}

Invoke-Case 'API-UP-003' '🔴 P0 分类大小上限：超限的 txt 被拒' {
    # txt 归 document，上限 20MB。用 21MB 的内容去顶。
    $big = New-Object byte[] (21 * 1024 * 1024)
    $r = Invoke-Upload (New-UploadFile 'big.txt' $big)
    return (-not $r.success) -and $r.message -match '超过|大小|上限'
}

Invoke-Case 'API-UP-004' '🔴 无令牌上传被拒（401，不是 200 放行）' {
    $multi = [System.Net.Http.MultipartFormDataContent]::new()
    $multi.Add([System.Net.Http.ByteArrayContent]::new($realPng), 'file', 'anon.png')
    $resp = $http.PostAsync("$Gateway/gateway/files/Upload", $multi).GetAwaiter().GetResult()
    return [int]$resp.StatusCode -eq 401
}

Invoke-Case 'API-UP-005' '🔴 上传结果回带 publicUrl 且可回源读取' {
    if (-not $script:uploadedUrl) { return $false }
    try {
        $r = Invoke-WebRequest "$Gateway$($script:uploadedUrl)" -UseBasicParsing -TimeoutSec 20
    } catch {
        Write-Host ("        回源异常: " + $_.Exception.Message) -ForegroundColor Red
        return $false
    }
    # Content-Type 取回来是 String[]，直接 `-match 'image'` 走的是数组语义，
    # 会在明明是 image/png 时判成 false —— 断言本身错了，不是功能坏了。
    $ct = ($r.Headers['Content-Type'] -join ',')
    # 断言 Content-Type 是**本条用例的核心**：回源接口以前一律发 octet-stream，
    # 浏览器会嗅探字节照样把图画出来，所以线上完全看不出问题，
    # 但新标签页打开变成下载、走严格 CSP 的环境直接被拦。
    return $r.StatusCode -eq 200 -and $ct -match '^image/'
}

# ---------- 清理 ----------
Remove-Item -LiteralPath $script:tmpDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n=== UP 汇总 ===" -ForegroundColor Cyan
Write-Host ("  通过: {0}  失败: {1}" -f $script:pass, $script:fail) -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
if ($script:fail) {
    $script:failures | ForEach-Object { Write-Host ("    - " + $_) }
    exit 1
}
Write-Host "  全部通过" -ForegroundColor Green
exit 0



