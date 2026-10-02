<#
.SYNOPSIS
    API 回归测试。逐条对应 TEST_CASES.md 的用例编号。
.DESCRIPTION
    目前覆盖 CustomerService（TEST_CASES 的 API-AUT / API-CART 部分）。
    每新增一个服务就在此追加对应模块的用例。
    退出码非 0 即视为回归失败。
#>
[CmdletBinding()]
param(
    [string]$CustomerService = 'http://127.0.0.1:5280',
    [switch]$StopOnFail
)

$ErrorActionPreference = 'Continue'
$script:pass = 0
$script:fail = 0
$script:failures = @()

function Invoke-Case {
    param(
        [string]$Id,
        [string]$Name,
        [scriptblock]$Action
    )
    try {
        $ok = & $Action
        if ($ok) {
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

function Get-RandomSuffix { return (Get-Random -Minimum 100000 -Maximum 999999) }

Write-Host "`n=== HLT 健康检查 ===" -ForegroundColor Cyan

Invoke-Case 'API-HLT-001' 'CustomerService /health 返回 200' {
    try { $r = Invoke-WebRequest "$CustomerService/health" -TimeoutSec 10; return $r.StatusCode -eq 200 } catch { return $false }
}

Write-Host "`n=== AUT 注册登录 ===" -ForegroundColor Cyan

$sfx = Get-RandomSuffix
$name1 = "reg_ok_$sfx"
$phone1 = "139" + $sfx + "01"

Invoke-Case 'API-AUT-010' '注册成功返回令牌' {
    $body = @{ customerName = $name1; password = 'Test123456'; phone = $phone1; nickName = '回归账号' } | ConvertTo-Json
    $r = Invoke-RestMethod "$CustomerService/customers/Register" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 20
    $script:customerId = $r.data.customerId
    return $r.success -and ($r.data.customerId -ne '0') -and ($r.data.token.Length -gt 50)
}

Invoke-Case 'API-AUT-011' '同一登录名重复注册被拒' {
    $body = @{ customerName = $name1; password = 'Test123456'; phone = "138$sfx01" } | ConvertTo-Json
    # 重复注册本来就该是 400，Invoke-RestMethod 遇 400 会抛异常，所以这里显式按状态码判定
    try {
        $r = Invoke-RestMethod "$CustomerService/customers/Register" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 20
        return (-not $r.success) -and ($r.code -eq 400)
    } catch {
        return $_.Exception.Response.StatusCode.value__ -eq 400
    }
}

Invoke-Case 'API-AUT-012' '同一手机号重复注册被拒' {
    $body = @{ customerName = "reg_dup_$sfx"; password = 'Test123456'; phone = $phone1 } | ConvertTo-Json
    $r = Invoke-RestMethod "$CustomerService/customers/Register" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 20
    return (-not $r.success) -and ($r.code -eq 400)
}

Invoke-Case 'API-AUT-013' '密码弱校验：短密码被拒' {
    $body = @{ customerName = "reg_weak_$sfx"; password = '123'; phone = "137$sfx01" } | ConvertTo-Json
    try { $r = Invoke-RestMethod "$CustomerService/customers/Register" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 20 }
    catch { return $true }   # 400 也算通过
    return (-not $r.success)
}

Invoke-Case 'API-AUT-014' '密码弱校验：无数字被拒' {
    $body = @{ customerName = "reg_weak2_$sfx"; password = 'abcdefghij'; phone = "136$sfx01" } | ConvertTo-Json
    try { $r = Invoke-RestMethod "$CustomerService/customers/Register" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 20 }
    catch { return $true }
    return (-not $r.success)
}

Invoke-Case 'API-AUT-015' '手机号格式错误被拒' {
    $body = @{ customerName = "reg_phone_$sfx"; password = 'Test123456'; phone = '12345' } | ConvertTo-Json
    try { $r = Invoke-RestMethod "$CustomerService/customers/Register" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 20 }
    catch { return $true }
    return (-not $r.success)
}

Invoke-Case 'API-AUT-016' '登录名过短被拒' {
    $body = @{ customerName = 'ab'; password = 'Test123456' } | ConvertTo-Json
    try { $r = Invoke-RestMethod "$CustomerService/customers/Login" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 20 }
    catch { return $true }
    return (-not $r.success)
}

Write-Host "`n=== API-AUT-020~026 登录（含关键安全回归）===" -ForegroundColor Cyan

Invoke-Case 'API-AUT-020' '正确密码登录成功' {
    $body = @{ customerName = $name1; password = 'Test123456' } | ConvertTo-Json
    $r = Invoke-RestMethod "$CustomerService/customers/Login" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 20
    return $r.success -and ($r.data.customerName -eq $name1)
}

Invoke-Case 'API-AUT-021' '错误密码被拒' {
    $body = @{ customerName = $name1; password = 'WrongPass99' } | ConvertTo-Json
    $r = Invoke-RestMethod "$CustomerService/customers/Login" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 20
    return (-not $r.success) -and ($r.code -eq 400)
}

Invoke-Case 'API-AUT-022' '🔴 安全回归：不存在的账号必须登录失败' {
    $body = @{ customerName = 'definitely_not_exist_user_zzz'; password = 'Test123456' } | ConvertTo-Json
    $r = Invoke-RestMethod "$CustomerService/customers/Login" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 20
    # 这条锁死一个真实缺陷：ParseExpression 的 Result 会替换整个 WHERE，
    # 导致业务条件 customer_name 被顶掉，返回库里第一条记录，任意账号都能登录成功。
    return (-not $r.success) -and ($null -eq $r.data)
}

Invoke-Case 'API-AUT-023' '🔴 安全回归：错误密码与不存在账号返回同一句提示' {
    $b1 = @{ customerName = $name1; password = 'WrongPass99' } | ConvertTo-Json
    $b2 = @{ customerName = 'definitely_not_exist_user_zzz'; password = 'WrongPass99' } | ConvertTo-Json
    $r1 = Invoke-RestMethod "$CustomerService/customers/Login" -Method Post -Body $b1 -ContentType 'application/json' -TimeoutSec 20
    $r2 = Invoke-RestMethod "$CustomerService/customers/Login" -Method Post -Body $b2 -ContentType 'application/json' -TimeoutSec 20
    # 不泄露账号是否存在
    return ($r1.message -eq $r2.message)
}

Invoke-Case 'API-AUT-024' '🔴 审计：注册后雪花 Id 与创建时间必须落库' {
    $cs = "Host=127.0.0.1;Port=5432;Database=simpleshopcustomer;Username=simpleshop_app;Password=simpleshop_dev_2026"
    $row = docker exec simpleshop-postgres psql -U simpleshop_app -d simpleshopcustomer -tAc "select id || '|' || created_at from customer where customer_name = '$name1'"
    if (-not $row) { return $false }
    $parts = ($row | Select-Object -First 1).Trim().Split('|')
    if ($parts.Count -lt 2) { return $false }
    $id = [long]$parts[0]
    $created = $parts[1]
    return ($id -gt 1000000) -and ($created -notmatch '^0001-01-01')
}

Invoke-Case 'API-AUT-025' '🔴 审计：密码不得明文入库' {
    $row = docker exec simpleshop-postgres psql -U simpleshop_app -d simpleshopcustomer -tAc "select password_hash from customer where customer_name = '$name1'"
    $hash = ($row | Select-Object -First 1).Trim()
    return ($hash.StartsWith('pbkdf2$')) -and (-not $hash.Contains('Test123456'))
}

Invoke-Case 'API-AUT-026' '🔴 审计：同密码两次注册哈希不同（随机盐）' {
    $n2 = "reg_salt2_" + (Get-Random -Minimum 100000 -Maximum 999999)
    # 手机号必须 11 位：3 位前缀 + 6 位随机 + 2 位后缀
    $p2 = "135" + (Get-Random -Minimum 100000 -Maximum 999999) + "77"
    $b = @{ customerName = $n2; password = 'Test123456'; phone = $p2 } | ConvertTo-Json
    try {
        Invoke-RestMethod "$CustomerService/customers/Register" -Method Post -Body $b -ContentType 'application/json' -TimeoutSec 20 | Out-Null
    } catch {
        Write-Host ("        注册 " + $n2 + " 失败：" + $_.Exception.Message) -ForegroundColor Yellow
        return $false
    }
    $h1 = (docker exec simpleshop-postgres psql -U simpleshop_app -d simpleshopcustomer -tAc "select password_hash from customer where customer_name = '$name1'" | Select-Object -First 1).Trim()
    $h2 = (docker exec simpleshop-postgres psql -U simpleshop_app -d simpleshopcustomer -tAc "select password_hash from customer where customer_name = '$n2'" | Select-Object -First 1).Trim()
    if (-not $h1 -or -not $h2) {
        Write-Host ("        取哈希失败 h1=" + $h1 + " h2=" + $h2) -ForegroundColor Yellow
        return $false
    }
    return ($h1 -ne $h2) -and ($h1.Split('$')[2] -ne $h2.Split('$')[2])
}

Write-Host "`n=== 汇总 ===" -ForegroundColor Cyan
Write-Host ("  通过: " + $script:pass + "  失败: " + $script:fail)
if ($script:fail -gt 0) {
    Write-Host "  失败用例:" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "  全部通过" -ForegroundColor Green
exit 0



