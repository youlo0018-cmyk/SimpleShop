<#
.SYNOPSIS
    由 deploy/shared/validation-rules.json 生成两端的校验规则常量。
.DESCRIPTION
    依据 CODING_STANDARD.md 3.5：校验规则只维护一份，构建时生成 C# 与 JS 常量。
    字段与规则的绑定关系仍由各端手写（Validator / validators.js），本脚本不生成绑定。
    修改 JSON 后必须重跑本脚本。
#>
[CmdletBinding()]
param([switch]$Quiet)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$jsonPath = Join-Path $root 'deploy\shared\validation-rules.json'

if (-not (Test-Path $jsonPath)) { throw "找不到规则文件: $jsonPath" }

if (-not $Quiet) { Write-Host '==> 读取规则' -ForegroundColor Cyan }
$doc = Get-Content -LiteralPath $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
$rules = $doc.rules
if (-not $rules) { throw '规则文件里没有 rules 节点。' }

function Get-Prop($obj, $name) { return $obj.PSObject.Properties[$name].Value }
function Escape-DotNet([string]$s) { return $s.Replace('\', '\\').Replace('"', '\"') }
function Escape-Js([string]$s) { return $s.Replace('\', '\\').Replace("'", "\'") }
function Get-Culture([string]$name) { return $name.Substring(0, 1).ToUpper() + $name.Substring(1) }

function Format-JsValue($value) {
    if ($value -is [bool]) { return $(if ($value) { 'true' } else { 'false' }) }
    if ($value -is [int] -or $value -is [long] -or $value -is [double]) { return [string]$value }
    return "'" + (Escape-Js ([string]$value)) + "'"
}

$utf8 = New-Object System.Text.UTF8Encoding $false

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('namespace Collaboration.Domain.Validation;')
[void]$sb.AppendLine()
[void]$sb.AppendLine('/// <summary>')
[void]$sb.AppendLine('/// 校验规则常量。由 scripts/generate-validation-rules.ps1 从')
[void]$sb.AppendLine('/// deploy/shared/validation-rules.json 生成，请勿手工修改。')
[void]$sb.AppendLine('/// </summary>')
[void]$sb.AppendLine('/// <remarks>单一来源见 CODING_STANDARD.md 3.5。改规则请改 JSON 后重跑生成脚本。</remarks>')
[void]$sb.AppendLine('public static class ValidationPatterns')
[void]$sb.AppendLine('{')

foreach ($p in $rules.PSObject.Properties) {
    $name = $p.Name
    $r = $p.Value
    [void]$sb.AppendLine()
    $pat = Get-Prop $r 'pattern'
    if ($pat) {
        [void]$sb.AppendLine('    /// <summary>' + $name + ' 规则的正则。</summary>')
        [void]$sb.AppendLine('    public const string ' + $name + 'Pattern = "' + (Escape-DotNet $pat) + '";')
    }
    $msg = Get-Prop $r 'message'
    if ($msg) {
        [void]$sb.AppendLine('    /// <summary>' + $name + ' 规则的提示文案。</summary>')
        [void]$sb.AppendLine('    public const string ' + $name + 'Message = "' + (Escape-DotNet $msg) + '";')
    }
    foreach ($k in @('min', 'max')) {
        $v = Get-Prop $r $k
        if ($null -ne $v) {
            $suffix = Get-Culture $k
            [void]$sb.AppendLine('    /// <summary>' + $name + ' 规则的' + $k + ' 边界值。</summary>')
            [void]$sb.AppendLine('    public const string ' + $name + $suffix + ' = "' + (Escape-DotNet ([string]$v)) + '";')
        }
    }
    foreach ($k in @('maxLength', 'minLength')) {
        $v = Get-Prop $r $k
        if ($null -ne $v) {
            $suffix = Get-Culture $k
            [void]$sb.AppendLine('    /// <summary>' + $name + ' 规则的' + $k + ' 边界值。</summary>')
            [void]$sb.AppendLine('    public const int ' + $name + $suffix + ' = ' + [int]$v + ';')
        }
    }
}

[void]$sb.AppendLine('}')

$csPath = Join-Path $root 'src\Collaboration\Collaboration.Domain\Validation\ValidationPatterns.cs'
New-Item -ItemType Directory -Path (Split-Path $csPath) -Force | Out-Null
[System.IO.File]::WriteAllText($csPath, $sb.ToString(), $utf8)

$js = [System.Text.StringBuilder]::new()
[void]$js.AppendLine('// 由 scripts/generate-validation-rules.ps1 从 deploy/shared/validation-rules.json 生成，请勿手工修改。')
[void]$js.AppendLine('// 单一来源见 CODING_STANDARD.md 3.5。')
[void]$js.AppendLine('export const VALIDATION_RULES = {')

foreach ($p in $rules.PSObject.Properties) {
    $name = $p.Name
    $r = $p.Value
    $parts = @()
    $pat = Get-Prop $r 'pattern'
    # JSON 里 "\\\\d" 解码后是 \d，直接放进 JS 正则字面量即可；这里不能再转义，
    # 否则会变成匹配字面反斜杠的错误正则。
    if ($pat) { $parts += 'pattern: /' + $pat + '/' }
    $msg = Get-Prop $r 'message'
    if ($msg) { $parts += 'message: ' + (Format-JsValue $msg) }
    foreach ($k in @('min', 'max', 'maxLength', 'minLength', 'type', 'requireLetter', 'requireDigit')) {
        $v = Get-Prop $r $k
        if ($null -ne $v) { $parts += $k + ' : ' + (Format-JsValue $v) }
    }
    [void]$js.AppendLine('  ' + $name + ' : { ' + ($parts -join ', ') + ' },')
}

[void]$js.AppendLine('};')
[void]$js.AppendLine('')
[void]$js.AppendLine('export default VALIDATION_RULES;')

$targets = @(
    (Join-Path $root 'apps\admin-vue\src\utils\validation-rules.js'),
    (Join-Path $root 'apps\user-uniapp\src\common\validation-rules.js')
)
foreach ($t in $targets) {
    New-Item -ItemType Directory -Path (Split-Path $t) -Force | Out-Null
    [System.IO.File]::WriteAllText($t, $js.ToString(), $utf8)
}

if (-not $Quiet) {
    $count = @($rules.PSObject.Properties).Count
    Write-Host "==> 已生成 $count 条规则" -ForegroundColor Green
    Write-Host "    $csPath"
    foreach ($t in $targets) { Write-Host "    $t" }
}



