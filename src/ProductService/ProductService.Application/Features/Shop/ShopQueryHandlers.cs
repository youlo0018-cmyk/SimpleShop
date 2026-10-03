using Collaboration.Domain.Common;
using ProductService.Application.Services;
using ProductService.Domain.Entities;
using ProductService.Domain.IRepository;

// `Features/Product` 这个命名空间会遮蔽同名的 Product 实体（CS0118），
// 必须在本地起别名。全项目统一这么处理，别的地方也一样。
using ProductEntity = ProductService.Domain.Entities.Product;

namespace ProductService.Application.Features.Shop;

/// <summary>前台商品分页处理器。</summary>
/// <remarks>
/// <para><b>审核通过 + 已上架由服务端强制过滤</b>，不靠调用方传参数。
/// 前台看到「已下架」或「未审核」的商品，轻则是运营事故、重则是能把没审过的商品买出去——
/// 所以过滤条件写死在服务端，命令里压根没有这两个字段。</para>
///
/// <para><b>到手价一次批量算</b>：先把当页商品的启用 SKU 一次查回来，
/// 再合成一次营销试算（BUSINESS.md 11.5 的「整页商品合并为一次批量试算」）。
/// 逐个商品调营销服务就是 N+1，一屏 20 个商品就是 20 次跨服务调用。</para>
/// </remarks>
public sealed class QueryShopProductsHandler
    : MediatR.IRequestHandler<QueryShopProductsCommand, ApiResponse<ShopProductPage>>
{
    private readonly IProductRepository _products;
    private readonly IShopPriceClient _prices;

    /// <summary>构造处理器。</summary>
    /// <param name="products">商品仓储。</param>
    /// <param name="prices">营销服务客户端（到手价）。</param>
    public QueryShopProductsHandler(IProductRepository products, IShopPriceClient prices)
    {
        _products = products;
        _prices = prices;
    }

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品分页结果，含到手价与优惠来源。</returns>
    public async Task<ApiResponse<ShopProductPage>> Handle(
        QueryShopProductsCommand request, CancellationToken ct)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 50);

        var filter = new ProductQuery
        {
            Keyword = request.Keyword ?? string.Empty,
            CategoryId = request.CategoryId,
            BrandId = request.BrandId,

            // 前台只看「审核通过 + 已上架」，写死在服务端
            AuditStatus = AuditStatuses.Approved,
            Status = ListingStatuses.OnShelf,

            Order = request.SortBy switch
            {
                ShopSorts.SalesDesc => ProductSorts.SalesDesc,
                ShopSorts.Newest => ProductSorts.Newest,
                _ => ProductSorts.Default
            }
        };

        var (rows, total) = await _products.QueryPagedAsync(page, pageSize, filter, ct);

        var items = await BuildItemsAsync(request.CustomerId, rows, ct);

        // 按到手价排序没法交给 SQL（要跨服务试算），只能在**当页**内排。
        // 这是已知取舍：这里排出来的只是当前页的相对顺序，
        // 不是全店最低价在前。真实需求量大时应改为「先试算再整体排序 + 缓存到手价」。
        if (request.SortBy is ShopSorts.PriceAsc or ShopSorts.PriceDesc)
        {
            items = request.SortBy == ShopSorts.PriceAsc
                ? items.OrderBy(a => a.FinalPrice).ThenBy(a => a.ProductId, StringComparer.Ordinal).ToList()
                : items.OrderByDescending(a => a.FinalPrice).ThenBy(a => a.ProductId, StringComparer.Ordinal).ToList();
        }

        return ApiResults.Ok(new ShopProductPage(items, total, page, pageSize));
    }

    /// <summary>给当页商品算到手价并组装列表项。</summary>
    /// <param name="customerId">客户 Id，0 表示游客。</param>
    /// <param name="rows">当页商品。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>列表项。</returns>
    private async Task<List<ShopProductItem>> BuildItemsAsync(
        long customerId, IReadOnlyList<ProductEntity> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return new List<ShopProductItem>();

        var priceRows = await _products
            .GetSkuPriceRowsAsync(rows.Select(a => a.Id).ToArray(), ct);

        var byProduct = priceRows
            .GroupBy(a => a.ProductId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // **每个 SKU 单独定价**，不把它们拍平成一单。
        // 拍平的坏处：「满 100 减 20」遇到 200 元与 100 元两个 SKU，门槛按合计 300 判过，
        // 20 元摊到两件上——200 元那件显示 186.67，而用户真只买那一个 SKU 时是 180。
        // 商品卡上的价低于实付价，用户结算时发现变贵，这就是标价不符投诉。
        // 按商品分组也不行：同一商品的不同规格之间同样会被摊，顾客买的是一件不是全部规格。
        var priceLines = priceRows
            .Select(a => new ShopPriceLine(a.ProductId, a.SkuId, a.Price))
            .ToList();

        var prices = await _prices.CalculateAsync(customerId, priceLines, ct);

        var items = new List<ShopProductItem>(rows.Count);

        foreach (var row in rows)
        {
            // 一个启用 SKU 都没有的商品不该出现在前台列表里：
            // 它展示不出任何可买的规格，用户点进去只能看到一个空壳。
            if (!byProduct.TryGetValue(row.Id, out var skus) || skus.Count == 0) continue;

            var originals = skus.Select(a => a.Price).ToList();
            var originalPrice = originals.Min();

            // 到手价取**各启用 SKU 的最小值**：商品卡显示的就是「最低能买到的那个价」。
            // 取的是最大值的话，卡片上的价比实际买得到的高，用户加购物车发现变贵了。
            var best = skus
                .Select(a => prices.TryGetValue(a.SkuId, out var p) ? p : new ShopSkuPrice(a.Price, a.Price, 0m, "none", string.Empty))
                .OrderBy(a => a.PayableAmount)
                .First();

            items.Add(new ShopProductItem(
                row.Id.ToString(),
                row.SpuName,
                row.SubTitle,
                row.MainImage,
                row.BrandName,
                row.CategoryName,
                row.DeliveryType,
                originalPrice,
                best.PayableAmount,
                best.Source,
                best.SourceName,
                best.DiscountAmount > 0m,
                row.Sales));
        }

        return items;
    }


}

/// <summary>前台商品详情处理器。</summary>
public sealed class QueryShopProductDetailHandler
    : MediatR.IRequestHandler<QueryShopProductDetailCommand, ApiResponse<ShopProductDetailDto>>
{
    private readonly IProductRepository _products;
    private readonly IShopPriceClient _prices;

    /// <summary>构造处理器。</summary>
    /// <param name="products">商品仓储。</param>
    /// <param name="prices">营销服务客户端（到手价）。</param>
    public QueryShopProductDetailHandler(IProductRepository products, IShopPriceClient prices)
    {
        _products = products;
        _prices = prices;
    }

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品详情，含每个 SKU 的到手价。</returns>
    /// <remarks>
    /// 未上架 / 未审核通过的商品一律回 404。前台能看到「存在但没上架」的商品，
    /// 等于告诉竞品「我们正在筹备 XX」，也等于让用户收藏一个买不到的东西。
    /// </remarks>
    public async Task<ApiResponse<ShopProductDetailDto>> Handle(
        QueryShopProductDetailCommand request, CancellationToken ct)
    {
        var product = await _products.GetByIdAsync(request.ProductId, ct);
        if (product is null
            || product.AuditStatus != AuditStatuses.Approved
            || product.Status != ListingStatuses.OnShelf)
        {
            return ApiResults.Fail<ShopProductDetailDto>(BaseApiResponseCode.NotFound, "商品不存在或已下架");
        }

        var specs = await _products.GetSpecsAsync(product.Id, ct);
        var values = await _products.GetSpecValuesAsync(product.Id, ct);
        var skus = await _products.GetSkusAsync(product.Id, ct);
        var links = await _products.GetSkuSpecLinksAsync(product.Id, ct);

        var enabled = skus.Where(a => a.Status == SkuStatuses.Enabled).ToList();

        var prices = enabled.Count == 0
            ? new Dictionary<long, ShopSkuPrice>()
            // 每个 SKU 单独定价：详情页展示的每个规格的到手价，
            // 必须就是用户选中那个规格下单时的到手价。
            : await _prices.CalculateAsync(
                request.CustomerId,
                enabled.Select(a => new ShopPriceLine(product.Id, a.Id, a.Price)).ToList(),
                ct);

        var linksBySku = links.GroupBy(a => a.SkuId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.SpecValueId).OrderBy(a => a).ToArray());

        var valuesBySpec = values.GroupBy(a => a.SpecId)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.SortOrder).ThenBy(a => a.Id).ToList());

        var specDtos = specs
            .OrderBy(a => a.SortOrder)
            .Select(spec => new ShopSpecDto(
                spec.Id,
                spec.SpecName,
                valuesBySpec.TryGetValue(spec.Id, out var vs)
                    ? vs.Select(v => new ShopSpecValueDto(v.Id, v.ValueName)).ToList()
                    : new List<ShopSpecValueDto>()))
            .ToList();

        var skuDtos = enabled.Select(sku =>
        {
            var price = prices.TryGetValue(sku.Id, out var p)
                ? p
                : new ShopSkuPrice(sku.Price, sku.Price, 0m, "none", string.Empty);

            return new ShopSkuDto(
                sku.Id.ToString(),
                sku.SkuName,
                sku.SkuSpecText,
                sku.Image,
                sku.Price,
                price.PayableAmount,
                price.Source,
                price.SourceName,
                linksBySku.TryGetValue(sku.Id, out var ids) ? ids : Array.Empty<long>());
        }).ToList();

        var dto = new ShopProductDetailDto(
            product.Id.ToString(),
            product.SpuName,
            product.SubTitle,
            product.MainImage,
            product.Images,
            product.DetailImages,
            product.BrandName,
            product.CategoryName,
            product.DeliveryType,
            specDtos,
            skuDtos);

        return ApiResults.Ok(dto);
    }
}