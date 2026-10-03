using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using ProductService.Application.Features.Shop;

namespace ProductService.Api.Controllers;

/// <summary>前台商品只读接口（小程序商品列表 / 详情）。</summary>
/// <remarks>
/// <para>与后台的 <see cref="ProductController"/> <b>刻意分成两个控制器</b>：
/// 后台要看到待审核、下架、包含审核状态的商品，前台一律只看「审核通过 + 已上架」。
/// 混在一个控制器里靠参数区分，早晚有人把后台接口暴露给小程序，
/// 于是用户看到「已下架」甚至能把没审过的商品买出去。</para>
///
/// <para>本组接口<b>不需要登录</b>：游客也能浏览商品（传 <c>customerId = 0</c> 时只算活动价不计券，
/// 见 BUSINESS.md 11.5）。要登录的是加购与下单，不是浏览。</para>
/// </remarks>
[ApiController]
[Route("shop/products")]
public sealed class ShopProductController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public ShopProductController(IMediator mediator) => _mediator = mediator;

    /// <summary>商品分页（只返回审核通过 + 已上架）。</summary>
    /// <param name="command">命令；CustomerId 传 0 即游客。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品分页，含到手价与优惠来源标签。</returns>
    /// <remarks>
    /// 展示规则是「<b>到手价 + 划线原价 + 优惠来源标签</b>」（DESIGN_SPEC 11.5）：
    /// 到手价取各启用 SKU 的**最小值**，而不是最大值——显示得比实际能买到的贵，
    /// 用户加购后发现变贵，投诉就是这么来的。
    /// </remarks>
    [HttpPost("List")]
    public Task<ApiResponse<ShopProductPage>> List(
        [FromBody] QueryShopProductsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>商品详情（只返回审核通过 + 已上架）。</summary>
    /// <param name="command">命令；CustomerId 传 0 即游客。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品详情，含规格、SKU 与每个 SKU 的到手价。</returns>
    /// <remarks>未上架 / 未审核通过一律回 404，不区分「不存在」与「已下架」。</remarks>
    [HttpPost("Detail")]
    public Task<ApiResponse<ShopProductDetailDto>> Detail(
        [FromBody] QueryShopProductDetailCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>商品搜索（Elasticsearch + IK 中文分词，无需登录）。</summary>
    /// <param name="command">命令；关键词留空即按类目 / 品牌浏览。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>搜索结果，含到手价与优惠来源。</returns>
    /// <remarks>
    /// <para>ES 只负责<b>召回商品 Id</b>，价格与上下架状态一律回库取权威值——
    /// 直接读索引副本会出现「搜到有货、点进去已下架」「列表 99、结算 129」，
    /// 这种不一致对价格信任的伤害比搜不准更大。</para>
    ///
    /// <para>搜不到时自动降级为「按类目浏览」，不返回错误：
    /// 搜索是增强功能，它不可用不该让商品页整个打不开。</para>
    /// </remarks>
    [HttpPost("Search")]
    public Task<ApiResponse<ShopSearchResult>> Search(
        [FromBody] QueryShopSearchCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}