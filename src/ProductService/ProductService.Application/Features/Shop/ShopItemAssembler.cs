using ProductService.Application.Services;
using ProductService.Domain.IRepository;
using ProductEntity = ProductService.Domain.Entities.Product;

namespace ProductService.Application.Features.Shop;

/// <summary>
/// 把商品组装成前台列表项（算到手价 + 优惠来源）。
/// </summary>
/// <remarks>
/// <para>抽出来是因为<b>到手价的算法是这个模块里最容易写错、也最难查的一处</b>：
/// 每个 SKU 必须单独定价，各行优惠额按比例分摊，余数给最小价那行。
/// 写成「按商品分组」或「把整页拍平成一单」都会让卡片价比实付价低，
/// 而用户只会在结算时才发现，然后来投诉。</para>
///
/// <para>列表查询与搜索查询共用同一份实现，就是为了让「两处算法的差异」这种 bug 没有藏身之处。</para>
/// </remarks>
public sealed class ShopItemAssembler
{
    private readonly IProductRepository _products;
    private readonly IShopPriceClient _prices;

    /// <summary>构造组装器。</summary>
    /// <param name="products">商品仓储。</param>
    /// <param name="prices">营销服务客户端（到手价）。</param>
    public ShopItemAssembler(IProductRepository products, IShopPriceClient prices)
    {
        _products = products;
        _prices = prices;
    }

    /// <summary>给一批商品组装前台列表项。</summary>
    /// <param name="customerId">客户 Id，0 表示游客（只算活动价不计券）。</param>
    /// <param name="rows">商品列表。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>列表项；<b>没有启用 SKU 的商品会被剔除</b>，且返回顺序与入参一致。</returns>
    /// <remarks>
    /// 没有启用 SKU 的商品直接剔除：它展示不出任何可买的规格，
    /// 用户点进去只能看到一个空壳——比不展示更糟。
    /// </remarks>
    public async Task<List<ShopProductItem>> BuildAsync(
        long customerId, IReadOnlyList<ProductEntity> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return new List<ShopProductItem>();

        var priceRows = await _products.GetSkuPriceRowsAsync(rows.Select(a => a.Id).ToArray(), ct);

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
            if (!byProduct.TryGetValue(row.Id, out var skus) || skus.Count == 0) continue;

            var originalPrice = skus.Min(a => a.Price);

            // 到手价取**各启用 SKU 的最小值**：商品卡显示的就是「最低能买到的那个价」。
            // 取最大值的话卡片上的价比实际买得到的高，用户加购物车发现变贵了。
            var best = skus
                .Select(a => prices.TryGetValue(a.SkuId, out var p)
                    ? p
                    : new ShopSkuPrice(a.Price, a.Price, 0m, "none", string.Empty))
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