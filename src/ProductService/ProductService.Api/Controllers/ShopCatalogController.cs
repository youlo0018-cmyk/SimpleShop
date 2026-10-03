using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using ProductService.Application.Features.Brand;
using ProductService.Application.Features.Category;

namespace ProductService.Api.Controllers;

/// <summary>前台分类树与品牌列表（小程序首页导航，无需登录）。</summary>
/// <remarks>
/// 与商品一样走独立前缀 `/shop/*`，和后台的 `/categories/*` `/brands/*` 分开。
/// 这两个接口<b>没有</b>「是否包含停用」这个参数——后台那个有，前台不给：
/// 一旦这个参数暴露在前台接口上，改一行 URL 就能看到运营已经下架的分类，
/// 而用户点进去是一片空白。与其加权限，不如根本不提供这个开关。
/// </remarks>
[ApiController]
[Route("shop/catalog")]
public sealed class ShopCatalogController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public ShopCatalogController(IMediator mediator) => _mediator = mediator;

    /// <summary>前台分类树（只含启用中的分类）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分类树，最多三级。</returns>
    [HttpGet("CategoryTree")]
    public Task<ApiResponse<List<CategoryNodeDto>>> CategoryTree(CancellationToken ct)
        // includeDisabled 写死 false：前台不接受「把停用的也给我」这种请求
        => _mediator.Send(new QueryCategoryTreeCommand(false), ct);

    /// <summary>前台品牌列表（只含启用中的品牌）。</summary>
    /// <param name="keyword">按品牌名模糊搜索，可为空。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>品牌列表。</returns>
    [HttpGet("Brands")]
    public Task<ApiResponse<List<BrandListItem>>> Brands(
        [FromQuery] string keyword = "", CancellationToken ct = default)
        // includeDisabled 同样写死 false；品牌列表不分页——前台导航一次要全量
        => _mediator.Send(new QueryBrandsCommand(1, 200, keyword, false), ct);
}