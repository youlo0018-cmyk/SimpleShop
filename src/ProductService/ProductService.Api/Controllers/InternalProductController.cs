using Collaboration.Domain.Common;
using FreeSql;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using ProductService.Application.Features.Shop;
using ProductService.Application.Features.Internal;
using ProductService.Domain.Entities;

namespace ProductService.Api.Controllers;

/// <summary>商品内部接口。网关不路由 /internal 前缀。</summary>
/// <remarks>
/// 购物车与订单都需要「SKU 的快照信息」（商品名 / 规格文本 / 图片 / 价格）。
/// 让它们各写一份查商品的代码不如给一个统一的内部查询：快照口径只有一处，
/// 以后加字段不用改三个服务。
/// </remarks>
[ApiController]
[Route("internal/products")]
public sealed class InternalProductController : ControllerBase
{
    private readonly IFreeSql _db;
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="db">FreeSql 实例。</param>
    /// <param name="mediator">MediatR 入口。</param>
    public InternalProductController(IFreeSql db, IMediator mediator)
    {
        _db = db;
        _mediator = mediator;
    }

    /// <summary>商品搜索索引对账（补偿任务调用）。</summary>
    /// <param name="command">对账命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>对账统计。</returns>
    /// <remarks>
    /// 索引写失败时商品保存仍会成功（ES 只是加速手段），代价是索引会慢慢和库不一致。
    /// 这个接口就是那条代价的兜底：**差集补写 + 清孤儿**，全程索引可搜，不做「删了重建」。
    /// </remarks>
    [HttpPost("search-index/sync")]
    public Task<ApiResponse<SearchIndexSyncResult>> SyncSearchIndex(
        [FromBody] SyncSearchIndexCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>回写商品评价均分与条数（EvaluateService 每日重算后调用）。</summary>
    /// <param name="command">回写命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回写统计。</returns>
    /// <remarks>
    /// 商品表上的评分是**冗余字段**：C 端列表页一次要展示几十个商品的评分，
    /// 逐个调评价服务既慢又让列表强依赖评价服务可用性。
    /// 代价是最多滞后 24 小时（每日 03:00 全量重算，规格 14.5）。
    /// </remarks>
    [HttpPost("ratings/sync")]
    public Task<ApiResponse<SyncProductRatingsResult>> SyncRatings(
        [FromBody] SyncProductRatingsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>批量下架某商户的全部已上架商品（商户审核被拒 / 停用时调用）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下架与索引同步统计。</returns>
    /// <remarks>
    /// <b>必须同步搜索索引</b>：搜索走 ES、<b>不走 C 端可见性过滤</b>。
    /// 不同步就会出现「商品页看不到但搜索搜得到，点进去才发现下架了」。
    /// 索引同步失败不回滚下架——商品已经在库里下架，严重度远低于「违规商品还在卖」，
    /// 失败计数返回给调用方并记警告，交给对账任务兜底。
    /// </remarks>
    [HttpPost("off-shelf-by-merchant")]
    public Task<ApiResponse<OffShelfByMerchantResult>> OffShelfByMerchant(
        [FromBody] OffShelfProductsByMerchantCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>校验一批商品能否被装修配置引用（装修页手动选品用）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可用与不可用的商品清单。</returns>
    /// <remarks>
    /// 装修是<b>直接面向顾客展示</b>的界面，手动指定商品的组件如果能挂未审核 / 未上架的商品，
    /// 审核机制就被装修页绕过了——运营自己就能把没过审的内容摆到首页。
    /// 判定：归属正确（本平台 / 本商户）+ 审核通过 + 已上架。
    /// <b>注意</b>：活动与券的适用商品不受此限制，规格 16.4 明确写了不要卡它们。
    /// </remarks>
    [HttpPost("check-for-design")]
    public Task<ApiResponse<CheckProductsForDesignResult>> CheckForDesign(
        [FromBody] CheckProductsForDesignCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>按 SKU Id 集合取快照信息。</summary>
    /// <param name="skuIds">SKU Id 集合，逗号分隔，最多 200 个。</param>
    /// <returns>命中的 SKU 快照列表。没命中的 SKU 不会出现在结果里。</returns>
    /// <remarks>
    /// 只返回**启用**的 SKU：购物车里留着已停用的 SKU 会让下单时算出奇怪的金额，
    /// 而下单时真正拦它的是库存与订单服务，这里先不给出明显不可用的数据。
    /// </remarks>
    [HttpGet("skus")]
    public ActionResult<ApiResponse<List<SkuSnapshot>>> Skus([FromQuery] string skuIds)
    {
        var ids = (skuIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => long.TryParse(a, out var v) ? v : 0)
            .Where(a => a > 0)
            .Distinct()
            .Take(200)
            .ToArray();

        if (ids.Length == 0)
        {
            return BadRequest(new { error = "invalid_request", error_description = "请提供至少一个 SKU Id" });
        }

        var skus = _db.Select<Sku>().Where(a => ids.Contains(a.Id) && a.Status == 1).ToList();

        // 配送方式挂在 SPU 上不在 SKU 上，但下单时要的是「这个 SKU 怎么送」，
        // 所以在这里 join 一次带出来，省得调用方自己再查一遍商品。
        var spuIds = skus.Select(a => a.ProductId).Distinct().ToArray();
        var deliveryBySpu = _db.Select<Product>()
            .Where(a => spuIds.Contains(a.Id))
            .ToList(a => new { a.Id, a.DeliveryType })
            .ToDictionary(a => a.Id, a => a.DeliveryType);

        var list = skus.Select(a => new SkuSnapshot(
            a.Id, a.ProductId, a.SkuCode, a.SkuName, a.SkuSpecText, a.Price, a.OriginalPrice, a.Image, a.Status,
            deliveryBySpu.GetValueOrDefault(a.ProductId, DeliveryTypes.PhysicalExpress)))
            .ToList();

        return Ok(ApiResults.Ok(list));
    }

    /// <summary>下单前的**权威定价与可售性**校验（订单服务调用）。</summary>
    /// <param name="skuIds">SKU Id 集合，逗号分隔，最多 50 个。</param>
    /// <returns>逐 SKU 的售价与可售状态；查不到的 SKU 不在结果里。</returns>
    /// <remarks>
    /// 与上面的 <c>skus</c> 有两处刻意不同：
    /// <list type="number">
    /// <item><b>不过滤停用 SKU</b>：要能区分「SKU 不存在」与「SKU 被停用」，
    /// 否则调用方只拿到一个空结果，报不出真实原因。</item>
    /// <item><b>带上 SPU 的审核与上下架状态</b>：下单必须拦下未审核 / 已下架的商品，
    /// 而这两项都挂在 SPU 上，不在 SKU 上。</item>
    /// </list>
    ///
    /// <para>这个接口存在的理由：下单请求里的 <c>unitPrice</c> 来自客户端。
    /// 没有它，订单服务只能相信客户端报的价格，于是把 25.50 的商品按 0.01 元下单也能成交。</para>
    /// </remarks>
    [HttpGet("skus/pricing")]
    public ActionResult<ApiResponse<List<SkuPricing>>> SkuPricing([FromQuery] string skuIds)
    {
        var ids = (skuIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => long.TryParse(a, out var v) ? v : 0)
            .Where(a => a > 0)
            .Distinct()
            .Take(50)
            .ToArray();

        if (ids.Length == 0)
        {
            return BadRequest(new { error = "invalid_request", error_description = "请提供至少一个 SKU Id" });
        }

        var skus = _db.Select<Sku>().Where(a => ids.Contains(a.Id)).ToList();
        var spuIds = skus.Select(a => a.ProductId).Distinct().ToArray();

        var spus = _db.Select<Product>()
            .Where(a => spuIds.Contains(a.Id))
            .ToList(a => new { a.Id, a.AuditStatus, a.Status, a.MerchantId, a.PlatformId, a.DeliveryType })
            .ToDictionary(a => a.Id);

        var list = skus.Select(a =>
        {
            spus.TryGetValue(a.ProductId, out var spu);
            return new SkuPricing(
                a.Id, a.ProductId, a.Price, a.Status,
                spu?.AuditStatus == AuditStatuses.Approved,
                spu?.Status == ListingStatuses.OnShelf,
                spu?.MerchantId ?? 0,
                spu?.PlatformId ?? 0,
                spu?.DeliveryType ?? DeliveryTypes.PhysicalExpress,

                // SkuName 在保存时就已经写成「商品名 规格」，不要再拼一次商品名，
                // 否则结算页会显示成「截图商品 截图商品 红」。
                a.SkuName,
                a.SkuSpecText,
                a.Image ?? string.Empty);
        }).ToList();

        return Ok(ApiResults.Ok(list));
    }

    /// <summary>按 Id 取物流公司（订单服务发货时用）。</summary>
    /// <param name="logisticsId">物流公司 Id。</param>
    /// <returns>物流公司 Id 与名称；查不到返回 404。</returns>
    /// <remarks>
    /// 订单服务发货时要往订单上写一份<b>公司名快照</b>，但物流公司字典在商品服务里。
    /// 让订单服务自己建一张表副本，或者信任前端传上来的公司名，都不行：
    /// 前端传的名字可以随便编，订单上就会留下一条查无此公司的物流记录。
    /// 所以这里由字典的归属方给出权威名称。
    ///
    /// <para><b>停用的公司仍然返回</b>：字典停用只影响「新建发货单时能不能选它」，
    /// 已经发出去的单必须还能查到公司名。</para>
    /// </remarks>
    [HttpGet("logistics-companies/{logisticsId:long}")]
    public ActionResult<ApiResponse<InternalLogisticsCompany>> LogisticsCompany(long logisticsId)
    {
        var company = _db.Select<LogisticsCompany>()
            .Where(a => a.Id == logisticsId)
            .First(a => new InternalLogisticsCompany(a.Id, a.CompanyName, a.Status));

        if (company is null)
        {
            return NotFound(new { error = "not_found", error_description = "物流公司不存在" });
        }

        return Ok(ApiResults.Ok(company));
    }
}

/// <summary>物流公司内部快照。</summary>
/// <param name="LogisticsId">物流公司 Id。</param>
/// <param name="CompanyName">公司名称，订单服务把它写成快照。</param>
/// <param name="Status">1 启用 / 2 停用。</param>
public sealed record InternalLogisticsCompany(long LogisticsId, string CompanyName, int Status);

/// <summary>SKU 快照信息。</summary>
/// <param name="SkuId">SKU Id。</param>
/// <param name="ProductId">所属商品 Id。</param>
/// <param name="SkuCode">SKU 编码。</param>
/// <param name="SkuName">SKU 名称（商品名 + 规格值）。</param>
/// <param name="SkuSpecText">规格文本，如「红色 / M」。</param>
/// <param name="Price">售价。</param>
/// <param name="OriginalPrice">划线原价。</param>
/// <param name="Image">SKU 图。</param>
/// <param name="Status">1 启用 / 2 停用。</param>
/// <param name="DeliveryType">配送方式，挂在 SPU 上。见 <see cref="DeliveryTypes"/>。</param>
public sealed record SkuSnapshot(
    long SkuId, long ProductId, string SkuCode, string SkuName, string SkuSpecText,
    decimal Price, decimal OriginalPrice, string Image, int Status, int DeliveryType);

/// <summary>SKU 权威定价与可售状态（订单服务下单前校验用）。</summary>
/// <param name="SkuId">SKU Id。</param>
/// <param name="ProductId">所属 SPU Id。</param>
/// <param name="Price">权威售价，两位小数。<b>订单金额一律以它为准</b>，不采信客户端。</param>
/// <param name="SkuEnabled">SKU 是否启用（1 启用 / 2 停用）。</param>
/// <param name="SpuApproved">SPU 是否审核通过。</param>
/// <param name="SpuOnShelf">SPU 是否已上架。</param>
/// <param name="MerchantId">归属商户 Id，0 表示平台自营。</param>
/// <param name="PlatformId">归属平台 Id。</param>
/// <param name="DeliveryType">配送方式，挂在 SPU 上。</param>
/// <param name="SkuName">商品名快照，结算试算要直接显示，不该让调用方自己再查一遍。</param>
/// <param name="SkuSpecText">规格文本快照。</param>
/// <param name="Image">SKU 图。</param>
public sealed record SkuPricing(
    long SkuId, long ProductId, decimal Price, int SkuEnabled,
    bool SpuApproved, bool SpuOnShelf, long MerchantId, long PlatformId, int DeliveryType,
    string SkuName, string SkuSpecText, string Image);
