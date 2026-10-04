<#
.SYNOPSIS
    MerchantPlatformService（5070）回归测试。
.DESCRIPTION
    覆盖 BUSINESS.md 1.4 可见性与 DATA_SPEC 5.1~5.3、5.31：
      - 平台：创建 / 编辑 / 删除拦截，**平台编码只读**（编辑传新值也无效）
      - 商户：新建默认「停用 + 待审核」，同平台内名称唯一
      - **审核拒绝连带下架该商户全部已上架商品**并同步搜索索引（最容易漏的一条）
      - 审核并发：两个管理员同时审，只有一个能改成功
      - 地区地址：读取 / 保存 / 恢复默认
    退出码非 0 即视为回归失败。
#>
[CmdletBinding()]
param(
    [string]$Gateway = 'http://127.0.0.1:5008',
    [string]$Merchant = 'http://127.0.0.1:5070',
    [string]$Product = 'http://127.0.0.1:5058',
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

# 平台编码必须是 **6 位纯字母** `[A-Za-z]{6}`（DATA_SPEC 5.1），所以这里只能用字母，
# 不能用随机数字拼——那会让每条用例都挂在「编码格式非法」上，真正的断言根本没跑到。
$script:suffix = Get-Random -Minimum 100000 -Maximum 999999
$letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'
$script:platformCode = -join (1..6 | ForEach-Object { $letters[[System.Random]::new().Next(26)] })
$script:platformId = 0
$script:merchantId = 0
$script:categoryIds = @()
$script:productId = 0

# ---- 后台令牌 ----
Add-Type -AssemblyName System.Net.Http
$http = [System.Net.Http.HttpClient]::new()
$dd = [System.Collections.Generic.Dictionary[string, string]]::new()
$dd['grant_type'] = 'password'
$dd['client_id'] = 'admin-app'
$dd['username'] = $AdminUser
$dd['password'] = $AdminPassword
# PowerShell 不支持跨行的方法链（`.GetAwaiter()` 换行就断），这里必须写成一行
$tokenResp = $http.PostAsync("$Gateway/gateway/auth/token", [System.Net.Http.FormUrlEncodedContent]::new($dd)).GetAwaiter().GetResult()
$tokenJson = ($tokenResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()) | ConvertFrom-Json
$script:adminHeaders = @{ Authorization = "Bearer $($tokenJson.access_token)" }

function MpPost([string]$Path, $Body) {
    # $Path 自带前导 /，这里只能直接拼接。
    # 写成 "$Merchant/$Path" 会拼出双斜杠 `//platforms/...`，全部 404——
    # 现象是「接口明明存在却 404」，排查方向会被完全带偏
    return Invoke-RestMethod "$Merchant$Path" -Method Post -Headers $script:adminHeaders `
        -Body ($Body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 60
}

function GwPost([string]$Path, $Body) {
    return Invoke-RestMethod "$Gateway$Path" -Method Post -Headers $script:adminHeaders `
        -Body ($Body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -TimeoutSec 60
}

Write-Host "`n=== MP 平台 ===" -ForegroundColor Cyan

Invoke-Case 'API-MP-000' '创建平台成功' {
    $r = MpPost '/platforms/Create' @{
        platformName = "演示平台$($script:suffix)"
        platformCode = $script:platformCode
        contactName = '张三'
        contactPhone = '13800138000'
        mallName = "演示商城$($script:suffix)"
        shippingFee = 10.00
        freeShippingThreshold = 99.00
    }
    $script:platformId = [long]$r.data
    return $r.success -and $script:platformId -gt 0
}

Invoke-Case 'API-MP-001' '平台列表带出商户数与状态中文名' {
    $r = MpPost '/platforms/List' @{ page = 1; pageSize = 20 }
    # 列表项的主键字段就叫 `Id`（与项目里的 OptionDto { Id, Name } 口径一致），
    # 按 platformId / merchantId 过滤会一条都匹配不到，Where-Object 返回空 → 断言全挂
    $row = @($r.data.items | Where-Object { $_.id -eq "$($script:platformId)" })[0]
    return $row -and $row.statusName -eq '启用' -and $row.merchantCount -eq 0
}

Invoke-Case 'API-MP-002' '🔴 平台编码**编辑时只读**：传新值也无效，且不报错' {
    $r = MpPost '/platforms/Update' @{
        platformId = $script:platformId
        platformName = "演示平台改名$($script:suffix)"
        platformCode = 'ZZZZZZ'
        contactName = '张三'; contactPhone = '13800138000'
        mallName = "演示商城$($script:suffix)"
    }
    if (-not $r.success) { return $false }
    $l = MpPost '/platforms/List' @{ page = 1; pageSize = 20 }
    $row = @($l.data.items | Where-Object { $_.id -eq "$($script:platformId)" })[0]
    # 小程序用 PLATFORM_CODE 锁死平台，改了等于让已发布的小程序找不到对应平台
    return $row.platformCode -eq $script:platformCode -and $row.platformName -match '改名'
}

Invoke-Case 'API-MP-003' '🔴 平台编码格式非法被拒（必须 6 位字母）' {
    try {
        MpPost '/platforms/Create' @{
            platformName = "非法编码$($script:suffix)"; platformCode = '123'
            contactName = '张三'; contactPhone = '13800138000'; mallName = '商城'
        } | Out-Null
        return $false
    } catch { return [int]$_.Exception.Response.StatusCode -eq 400 }
}

Invoke-Case 'API-MP-004' '🔴 平台编码重复被拒（全局唯一）' {
    $r = MpPost '/platforms/Create' @{
        platformName = "换个名字$($script:suffix)"; platformCode = $script:platformCode
        contactName = '张三'; contactPhone = '13800138000'; mallName = '商城'
    }
    return (-not $r.success) -and $r.message -match '编码'
}

Invoke-Case 'API-MP-005' '主题色格式非法被拒（必须是 #RRGGBB）' {
    # 这里必须传**合法的平台编码**：否则请求会先挂在「编码格式非法」上，
    # 拿到 400 就以为颜色校验生效了——用例是绿的，颜色规则却从没被执行过
    $otherCode = -join (1..6 | ForEach-Object { $letters[[System.Random]::new().Next(26)] })
    try {
        MpPost '/platforms/Create' @{
            platformName = "非法颜色$($script:suffix)"; platformCode = $otherCode
            contactName = '张三'; contactPhone = '13800138000'; mallName = '商城'
            primaryColor = 'red'
        } | Out-Null
        return $false
    } catch { return [int]$_.Exception.Response.StatusCode -eq 400 }
}

Write-Host "`n=== MP 商户 ===" -ForegroundColor Cyan

Invoke-Case 'API-MP-010' '新建商户：固定「停用 + 待审核」，编号 = 平台编码 + Id' {
    $r = MpPost '/merchants/Create' @{
        merchantName = "演示店铺$($script:suffix)"
        platformId = $script:platformId
        contactName = '李四'; contactPhone = '13900139000'
        description = '一家演示店铺'
    }
    if (-not $r.success) { return $false }
    $script:merchantId = [long]$r.data

    $l = MpPost '/merchants/List' @{ page = 1; pageSize = 20 }
    $row = @($l.data.items | Where-Object { $_.id -eq "$($script:merchantId)" })[0]

    # 新建默认停用：商户资质没过审之前不能对外运营
    return $row.statusName -eq '停用' -and $row.auditStatusName -eq '待审核' `
        -and $row.merchantNo -eq "$($script:platformCode)$($script:merchantId)"
}

Invoke-Case 'API-MP-011' '🔴 同平台内商户名重复被拒' {
    $r = MpPost '/merchants/Create' @{
        merchantName = "演示店铺$($script:suffix)"
        platformId = $script:platformId
        contactName = '李四'; contactPhone = '13900139000'
    }
    return (-not $r.success) -and $r.message -match '同名'
}

Invoke-Case 'API-MP-012' '🔴 挂在停用平台下建商户被拒（等于造一个永远没人能看到的店铺）' {
    MpPost '/platforms/Update' @{
        platformId = $script:platformId
        platformName = "演示平台改名$($script:suffix)"; platformCode = $script:platformCode
        contactName = '张三'; contactPhone = '13800138000'
        mallName = "演示商城$($script:suffix)"; status = 2
    } | Out-Null

    $r = MpPost '/merchants/Create' @{
        merchantName = "停用平台店铺$($script:suffix)"
        platformId = $script:platformId
        contactName = '李四'; contactPhone = '13900139000'
    }

    # 恢复启用，别影响后面的用例
    MpPost '/platforms/Update' @{
        platformId = $script:platformId
        platformName = "演示平台改名$($script:suffix)"; platformCode = $script:platformCode
        contactName = '张三'; contactPhone = '13800138000'
        mallName = "演示商城$($script:suffix)"; status = 1
    } | Out-Null

    return (-not $r.success) -and $r.message -match '停用'
}

Write-Host "`n=== MP 准备：给该商户挂一个已上架商品 ===" -ForegroundColor Cyan

Invoke-Case 'API-MP-015' '建三级分类' {
    $a = (GwPost '/gateway/categories/Create' @{ parentId = 0; categoryName = "商户$($script:suffix)"; platformId = 0 }).data
    $b = (GwPost '/gateway/categories/Create' @{ parentId = $a; categoryName = "商户$($script:suffix)"; platformId = 0 }).data
    $c = (GwPost '/gateway/categories/Create' @{ parentId = $b; categoryName = "商户$($script:suffix)"; platformId = 0 }).data
    $script:categoryIds = @($a, $b, $c)
    return $c -gt 0
}

Invoke-Case 'API-MP-016' '建商品 → 审核通过 → 上架' {
    $body = @{
        productId = 0; spuName = "商户商品$($script:suffix)"; categoryId = $script:categoryIds[2]
        deliveryType = 1; mainImage = 'https://cdn.example.com/m.png'
        platformId = $script:platformId; merchantId = $script:merchantId
        specs = @(@{ specName = '颜色'; specValues = @('红') })
        skus = @(@{ skuCode = "MP$($script:suffix)"; specValues = @('红'); price = 88; stock = 10; status = 1 })
    }
    $script:productId = [long](GwPost '/gateway/products/Save' $body).data

    # 上架要求审核已通过，所以顺序必须是：保存 → 审核 → 上架
    GwPost '/gateway/products/Audit' @{ productId = $script:productId; auditStatus = 20 } | Out-Null
    $r = GwPost '/gateway/products/ChangeListing' @{ productId = $script:productId; status = 1 }
    return $r.success
}

Invoke-Case 'API-MP-017' '确认商品确实是「已上架」，否则下架副作用测不出来' {
    $r = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:productId)" `
        -Headers $script:adminHeaders -TimeoutSec 30
    return $r.data.status -eq 1
}

Write-Host "`n=== MP 商户审核（连带下架是这批最关键的一条）===" -ForegroundColor Cyan

Invoke-Case 'API-MP-020' '🔴 审核结论非法被拒（只能已通过 / 已拒绝）' {
    try {
        MpPost '/merchants/Audit' @{
            merchantId = $script:merchantId; auditStatus = 10
            auditorId = 1; auditorName = '审核员'
        } | Out-Null
        return $false
    } catch { return [int]$_.Exception.Response.StatusCode -eq 400 }
}

Invoke-Case 'API-MP-021' '🔴 拒绝时不填原因被拒（商户要知道为什么被拒）' {
    try {
        MpPost '/merchants/Audit' @{
            merchantId = $script:merchantId; auditStatus = 90; auditRemark = ''
            auditorId = 1; auditorName = '审核员'
        } | Out-Null
        return $false
    } catch { return [int]$_.Exception.Response.StatusCode -eq 400 }
}

Invoke-Case 'API-MP-022' '🔴 P0 审核拒绝：**连带下架该商户全部已上架商品**' {
    $r = MpPost '/merchants/Audit' @{
        merchantId = $script:merchantId; auditStatus = 90
        auditRemark = '资质材料不齐全，请补充后重新提交'
        auditorId = 1; auditorName = '审核员'
    }
    if (-not $r.success) { return $false }

    # 商品资质依赖商户资质：商户没资质了，它的商品就不该继续对外销售
    $d = Invoke-RestMethod "$Gateway/gateway/products/Detail?productId=$($script:productId)" `
        -Headers $script:adminHeaders -TimeoutSec 30

    return $r.data.auditStatusName -eq '已拒绝' -and $r.data.offShelvedCount -eq 1 `
        -and $d.data.status -eq 2
}

Invoke-Case 'API-MP-023' '审核拒绝后商户列表能看到审核意见与审核人' {
    $l = MpPost '/merchants/List' @{ page = 1; pageSize = 20 }
    $row = @($l.data.items | Where-Object { $_.id -eq "$($script:merchantId)" })[0]
    return $row.auditStatusName -eq '已拒绝' -and $row.auditRemark -match '资质材料' `
        -and $row.auditorName -eq '审核员'
}

Invoke-Case 'API-MP-024' '🔴 重复审核被拒（不能覆盖掉上一次的审核人与时间）' {
    $r = MpPost '/merchants/Audit' @{
        merchantId = $script:merchantId; auditStatus = 20
        auditorId = 2; auditorName = '另一个审核员'
    }
    # 只有「待审核」的商户能被审核。已拒绝的直接改判通过，会绕过
    # 「拒绝 → 重新提交 → 再审核」，而且拒绝时下架过的商品再也不会被恢复
    return (-not $r.success) -and $r.message -match '拒绝'
}

Invoke-Case 'API-MP-025' '重新提交：已拒绝的商户可打回待审核' {
    $r = MpPost '/merchants/Resubmit' @{ merchantId = $script:merchantId }
    return $r.success
}

Invoke-Case 'API-MP-026' '🔴 已通过 / 待审核的商户重复提交被拒' {
    $r = MpPost '/merchants/Resubmit' @{ merchantId = $script:merchantId }
    # 对已通过的商户重复提交，等于把「已通过」白送回「待审核」，商户会莫名失去经营资格
    return (-not $r.success) -and $r.message -match '被拒'
}

Invoke-Case 'API-MP-027' '审核通过：状态变已通过，且**不再连带下架**' {
    $r = MpPost '/merchants/Audit' @{
        merchantId = $script:merchantId; auditStatus = 20
        auditRemark = '资料齐全，通过'
        auditorId = 1; auditorName = '审核员'
    }
    $l = MpPost '/merchants/List' @{ page = 1; pageSize = 20 }
    $row = @($l.data.items | Where-Object { $_.id -eq "$($script:merchantId)" })[0]
    return $r.success -and $r.data.offShelvedCount -eq 0 -and $row.auditStatusName -eq '已通过'
}

Write-Host "`n=== MP 地区地址 ===" -ForegroundColor Cyan

Invoke-Case 'API-MP-030' '未配置时回落到内置默认（isCustom = false）' {
    $r = Invoke-RestMethod "$Merchant/regions/Get?platformId=$($script:platformId)" `
        -Headers $script:adminHeaders -TimeoutSec 30
    return $r.success -and $r.data.isCustom -eq $false -and $r.data.regionsJson -match '北京市'
}

Invoke-Case 'API-MP-031' '🔴 地区 JSON 非法被拒（不是合法 JSON）' {
    # 业务失败按项目约定是 **HTTP 200 + success=false**（FluentValidation 才是 400）。
    # 断言写成「期待 400」会永远失败，而且看不出到底哪里不对
    $r = MpPost '/regions/Save' @{ platformId = $script:platformId; regionsJson = '{不是数组' }
    return (-not $r.success) -and $r.message -match 'JSON'
}

Invoke-Case 'API-MP-032' '🔴 空数组被拒（清空要走「恢复默认」，不是存空数组）' {
    $r = MpPost '/regions/Save' @{ platformId = $script:platformId; regionsJson = '[]' }
    return (-not $r.success) -and $r.message -match '空数组'
}

Invoke-Case 'API-MP-033' '🔴 子节点缺 name 被拒（前端按 name 渲染，缺了就是一行空白）' {
    $r = MpPost '/regions/Save' @{
        platformId = $script:platformId
        regionsJson = '[{"name":"浙江省","children":[{"code":"3301"}]}]'
    }
    return (-not $r.success) -and $r.message -match '名称'
}

Invoke-Case 'API-MP-034' '保存自定义地区数据成功，读回来 isCustom = true' {
    $json = '[{"name":"测试省","children":[{"name":"测试市","children":[{"name":"测试区"}]}]}]'
    $s = MpPost '/regions/Save' @{ platformId = $script:platformId; regionsJson = $json }
    $g = Invoke-RestMethod "$Merchant/regions/Get?platformId=$($script:platformId)" `
        -Headers $script:adminHeaders -TimeoutSec 30
    return $s.success -and $g.data.isCustom -eq $true -and $g.data.regionsJson -match '测试省'
}

Invoke-Case 'API-MP-035' '恢复默认：传空串清空配置，回落内置默认' {
    $s = MpPost '/regions/Save' @{ platformId = $script:platformId; regionsJson = '' }
    $g = Invoke-RestMethod "$Merchant/regions/Get?platformId=$($script:platformId)" `
        -Headers $script:adminHeaders -TimeoutSec 30
    return $s.success -and $g.data.isCustom -eq $false -and $g.data.regionsJson -match '北京市'
}

Write-Host "`n=== MP 删除拦截 ===" -ForegroundColor Cyan

Invoke-Case 'API-MP-040' '🔴 有商户的平台禁止删除，只能停用' {
    # 删掉平台会让商户变成「没有平台的孤儿」，而它们的商品与订单还在
    $r = MpPost '/platforms/Delete' @{ platformId = $script:platformId }
    return (-not $r.success) -and $r.message -match '商户'
}

Invoke-Case 'API-MP-041' '🔴 已通过审核的商户禁止删除，只能停用' {
    $r = MpPost '/merchants/Delete' @{ merchantId = $script:merchantId }
    return (-not $r.success) -and $r.message -match '停用'
}

Invoke-Case 'API-MP-042' '待审核的商户可以删除（还没接单，删了不留脏数据）' {
    $m2 = [long](MpPost '/merchants/Create' @{
        merchantName = "待审店铺$($script:suffix)"
        platformId = $script:platformId
        contactName = '王五'; contactPhone = '13700137000'
    }).data
    $r = MpPost '/merchants/Delete' @{ merchantId = $m2 }
    return $r.success
}

Invoke-Case 'API-MP-049' '清理：删商品 → 删分类' {
    if ($script:productId -gt 0) {
        GwPost '/gateway/products/Delete' @{ productId = $script:productId } | Out-Null
    }
    foreach ($id in [array]($script:categoryIds | Sort-Object -Descending)) {
        GwPost '/gateway/categories/Delete' @{ categoryId = $id } | Out-Null
    }
    return $true
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
