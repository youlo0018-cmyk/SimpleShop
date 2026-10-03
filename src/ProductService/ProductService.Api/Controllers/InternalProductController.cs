using Collaboration.Domain.Common;
using FreeSql;
using Microsoft.AspNetCore.Mvc;
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

    /// <summary>构造控制器。</summary>
    /// <param name="db">FreeSql 实例。</param>
    public InternalProductController(IFreeSql db) => _db = db;

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

        var list = skus.Select(a => new SkuSnapshot(
            a.Id, a.ProductId, a.SkuCode, a.SkuName, a.SkuSpecText, a.Price, a.OriginalPrice, a.Image, a.Status))
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
public sealed record SkuSnapshot(
    long SkuId, long ProductId, string SkuCode, string SkuName, string SkuSpecText,
    decimal Price, decimal OriginalPrice, string Image, int Status);