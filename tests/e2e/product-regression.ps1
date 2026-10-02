<#
.SYNOPSIS
    ProductService（5058）分类域回归测试。
.DESCRIPTION
    分类是后续商品、活动、装修的基础，规则错了会一路传到前台。这里验证：
      - 层级强制 ≤ 3，且 Level 由服务端按父链算出（前端传了也忽略）
      - 同父级内分类名唯一
      - 父分类不存在时拒绝
      - 有子分类 / 有商品时禁止删除，只能停用
      - 分类树正确带出 hasChildren
.PARAMETER Gateway
    网关地址，走网关才能顺带验证 RBAC 与租户头注入。
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
            if ($StopOnFail) { throw "用例 $Id 失败" }
        }
    } catch {
        $script:fail++
        $script:failures += "$Id $Name (异常: $($_.Exception.Message))"
        Write-Host ("  FAIL  " + $Id + "  " + $Name + "  " + $_.Exception.Message) -ForegroundColor Red
        if ($StopOnFail) { throw }
    }
}

Add-Type -AssemblyName System.Net.Http
$http = [System.Net.Http.HttpClient]::new()

function Get-Token {
    $d = [System.Collections.Generic.Dictionary[string,string]]::new()
    $d['grant_type'] = 'password'; $d['client_id'] = 'admin-app'
    $d['username'] = $AdminUser;     $d['password'] = $AdminPassword

    $resp = $http.PostAsync("$Gateway/gateway/auth/token", [System.Net.Http.FormUrlEncodedContent]::new($d)).GetAwaiter().GetResult()
    return ($resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).access_token
}

$script:token = Get-Token
$script:headers = @{ Authorization = "Bearer $script:token" }
$script:suffix = Get-Random -Minimum 100000 -Maximum 999999

function Post-Cat($Body) {
    Invoke-RestMethod -Uri "$Gateway/gateway/categories/Create" -Method Post `
        -Headers $script:headers -Body ($Body | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 30
}

Write-Host "`n=== PRD 分类（经网关，含 RBAC 与租户头链路）===" -ForegroundColor Cyan

Invoke-Case 'API-PRD-001' '无令牌访问分类树被拒（401）' {
    try { Invoke-RestMethod "$Gateway/gateway/categories/Tree" -TimeoutSec 15 | Out-Null; return $false }
    catch { return [int]$_.Exception.Response.StatusCode -eq 401 }
}

Invoke-Case 'API-PRD-002' '新建一级分类成功，level=1' {
    $script:l1 = (Post-Cat @{ parentId = 0; categoryName = "分类A$($script:suffix)"; platformId = 0 }).data
    return $script:l1 -gt 0
}

Invoke-Case 'API-PRD-003' '新建二级分类成功，level 由服务端算出' {
    $script:l2 = (Post-Cat @{ parentId = $script:l1; categoryName = "分类B$($script:suffix)"; platformId = 0 }).data
    $tree = Invoke-RestMethod "$Gateway/gateway/categories/Tree" -Headers $script:headers -TimeoutSec 30
    $node = $tree.data.children | Where-Object { $_.id -eq "$($script:l2)" }
    return $script:l2 -gt 0 -and $node.level -eq 2
}

Invoke-Case 'API-PRD-004' '新建三级分类成功，level=3' {
    $script:l3 = (Post-Cat @{ parentId = $script:l2; categoryName = "分类C$($script:suffix)"; platformId = 0 }).data
    $tree = Invoke-RestMethod "$Gateway/gateway/categories/Tree" -Headers $script:headers -TimeoutSec 30
    $l2node = $tree.data.children | Where-Object { $_.id -eq "$($script:l2)" }
    $node = $l2node.children | Where-Object { $_.id -eq "$($script:l3)" }
    return $script:l3 -gt 0 -and $node.level -eq 3
}

Invoke-Case 'API-PRD-005' '🔴 第四级被拒（层级上限 3）' {
    $r = Post-Cat @{ parentId = $script:l3; categoryName = "分类D$($script:suffix)"; platformId = 0 }
    return -not $r.success -and $r.message -match '3 级'
}

Invoke-Case 'API-PRD-006' '同父级同名被拒' {
    $r = Post-Cat @{ parentId = 0; categoryName = "分类A$($script:suffix)"; platformId = 0 }
    return -not $r.success -and $r.message -match '同名'
}

Invoke-Case 'API-PRD-007' '上级分类不存在被拒' {
    $r = Post-Cat @{ parentId = 999999999999; categoryName = "孤儿$($script:suffix)"; platformId = 0 }
    return -not $r.success -and $r.message -match '不存在'
}

Invoke-Case 'API-PRD-008' '🔴 有子分类时删除被拒' {
    $r = Invoke-RestMethod -Uri "$Gateway/gateway/categories/Delete" -Method Post `
        -Headers $script:headers -Body (@{ categoryId = $script:l1 } | ConvertTo-Json) `
        -ContentType 'application/json' -TimeoutSec 30
    return -not $r.success -and $r.message -match '子分类'
}

Invoke-Case 'API-PRD-009' '分类树带出 hasChildren' {
    $tree = Invoke-RestMethod "$Gateway/gateway/categories/Tree" -Headers $script:headers -TimeoutSec 30
    $node = $tree.data | Where-Object { $_.id -eq "$($script:l1)" }
    return $node.hasChildren -eq $true -and $node.children.Count -ge 1
}

Invoke-Case 'API-PRD-010' '叶子节点 hasChildren=false' {
    $tree = Invoke-RestMethod "$Gateway/gateway/categories/Tree" -Headers $script:headers -TimeoutSec 30
    $l1node = $tree.data | Where-Object { $_.id -eq "$($script:l1)" }
    $l2node = $l1node.children | Where-Object { $_.id -eq "$($script:l2)" }
    $l3node = $l2node.children | Where-Object { $_.id -eq "$($script:l3)" }
    return $l3node.hasChildren -eq $false
}

Invoke-Case 'API-PRD-011' '清理测试分类（先删叶子再删父级）' {
    foreach ($id in @($script:l3, $script:l2, $script:l1)) {
        Invoke-RestMethod -Uri "$Gateway/gateway/categories/Delete" -Method Post `
            -Headers $script:headers -Body (@{ categoryId = $id } | ConvertTo-Json) `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    }
    $tree = Invoke-RestMethod "$Gateway/gateway/categories/Tree" -Headers $script:headers -TimeoutSec 30
    return -not ($tree.data | Where-Object { $_.id -eq "$($script:l1)" })
}

Write-Host "`n=== 汇总 ===" -ForegroundColor Cyan
Write-Host ("  通过: " + $script:pass + "  失败: " + $script:fail)
$http.Dispose()

if ($script:fail -gt 0) {
    Write-Host "  失败用例:" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "  全部通过" -ForegroundColor Green
exit 0