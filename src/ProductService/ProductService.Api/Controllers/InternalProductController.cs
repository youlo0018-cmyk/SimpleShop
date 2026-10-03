using Collaboration.Domain.Common;
using FreeSql;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using ProductService.Application.Features.Shop;
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
}

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
