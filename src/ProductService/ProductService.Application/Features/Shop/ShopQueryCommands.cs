using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace ProductService.Application.Features.Shop;

/// <summary>前台商品分页（只返回**审核通过 + 已上架**的商品）。</summary>
/// <param name="CustomerId">客户 Id；传 0 表示游客，优惠只算活动不算券。</param>
/// <param name="Keyword">按商品名模糊搜索。</param>
/// <param name="CategoryId">按分类过滤，0 不限。</param>
/// <param name="BrandId">按品牌过滤，0 不限。</param>
/// <param name="Page">页码。</param>
/// <param name="PageSize">每页条数。</param>
/// <param name="SortBy">排序方式，见 <see cref="ShopSorts"/>。</param>
public record QueryShopProductsCommand(
    long CustomerId = 0,
    string Keyword = "",
    long CategoryId = 0,
    long BrandId = 0,
    int Page = 1,
    int PageSize = 20,
    int SortBy = ShopSorts.Default) : IRequest<ApiResponse<ShopProductPage>>;

/// <summary>前台商品详情（只返回审核通过 + 已上架的商品）。</summary>
/// <param name="CustomerId">客户 Id；0 表示游客。</param>
/// <param name="ProductId">商品 Id。</param>
public record QueryShopProductDetailCommand(long CustomerId, long ProductId)
    : IRequest<ApiResponse<ShopProductDetailDto>>;

/// <summary>前台商品搜索（Elasticsearch + IK 分词，无需登录）。</summary>
/// <param name="CustomerId">客户 Id；0 表示游客，只算活动价不计券。</param>
/// <param name="Keyword">关键词，空表示按类目 / 品牌浏览。</param>
/// <param name="CategoryId">分类过滤，0 表示不限。</param>
/// <param name="BrandId">品牌过滤，0 表示不限。</param>
/// <param name="Page">页码。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryShopSearchCommand(
    long CustomerId = 0,
    string Keyword = "",
    long CategoryId = 0,
    long BrandId = 0,
    int Page = 1,
    int PageSize = 20) : IRequest<ApiResponse<ShopSearchResult>>;

/// <summary>前台搜索结果。</summary>
/// <param name="Items">商品列表。</param>
/// <param name="Total">命中总数（按 ES 的 hits 总数估算，可能略偏）。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
/// <param name="Keyword">回显的关键词。</param>
/// <param name="Engine">检索引擎说明，便于排查「为什么搜不准」。</param>
public sealed record ShopSearchResult(
    IReadOnlyList<ShopProductItem> Items, long Total, int Page, int PageSize,
    string Keyword, string Engine);

/// <summary>前台排序方式。</summary>
public static class ShopSorts
{
    /// <summary>默认：综合（排序值 + 销量）。</summary>
    public const int Default = 0;

    /// <summary>销量从高到低。</summary>
    public const int SalesDesc = 1;

    /// <summary>价格从低到高（按到手价）。</summary>
    public const int PriceAsc = 2;

    /// <summary>价格从高到低（按到手价）。</summary>
    public const int PriceDesc = 3;

    /// <summary>最新上架。</summary>
    public const int Newest = 4;
}

/// <summary>前台商品列表项。</summary>
/// <param name="ProductId">商品 Id（字符串形式，雪花 Id 避免前端丢精度）。</param>
/// <param name="SpuName">商品名。</param>
/// <param name="SubTitle">副标题。</param>
/// <param name="MainImage">主图。</param>
/// <param name="BrandName">品牌名。</param>
/// <param name="CategoryName">分类名。</param>
/// <param name="DeliveryType">配送方式。</param>
/// <param name="OriginalPrice">划线原价：各启用 SKU 原价的最小值。</param>
/// <param name="FinalPrice">到手价：各启用 SKU 到手价的最小值。</param>
/// <param name="DiscountSource">优惠来源标签，无优惠为 <c>none</c>。</param>
/// <param name="DiscountSourceName">优惠来源名称（活动名或券码）。</param>
/// <param name="HasDiscount">是否有优惠。</param>
/// <param name="Sales">销量。</param>
public sealed record ShopProductItem(
    string ProductId, string SpuName, string SubTitle, string MainImage,
    string BrandName, string CategoryName, int DeliveryType,
    decimal OriginalPrice, decimal FinalPrice,
    string DiscountSource, string DiscountSourceName, bool HasDiscount, long Sales);

/// <summary>前台商品分页结果。</summary>
/// <param name="Items">当页商品。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record ShopProductPage(
    IReadOnlyList<ShopProductItem> Items, long Total, int Page, int PageSize);

/// <summary>前台商品详情。</summary>
/// <param name="ProductId">商品 Id。</param>
/// <param name="SpuName">商品名。</param>
/// <param name="SubTitle">副标题。</param>
/// <param name="MainImage">主图。</param>
/// <param name="Images">图集 JSON。</param>
/// <param name="DetailImages">详情图 JSON。</param>
/// <param name="BrandName">品牌名。</param>
/// <param name="CategoryName">分类名。</param>
/// <param name="DeliveryType">配送方式。</param>
/// <param name="Specs">规格项。</param>
/// <param name="Skus">SKU 列表，含每个 SKU 的到手价。</param>
public sealed record ShopProductDetailDto(
    string ProductId, string SpuName, string SubTitle,
    string MainImage, string Images, string DetailImages,
    string BrandName, string CategoryName, int DeliveryType,
    IReadOnlyList<ShopSpecDto> Specs, IReadOnlyList<ShopSkuDto> Skus);

/// <summary>前台规格项。</summary>
/// <param name="SpecId">规格项 Id。</param>
/// <param name="SpecName">规格名（颜色 / 尺码）。</param>
/// <param name="Values">规格值。</param>
public sealed record ShopSpecDto(long SpecId, string SpecName, IReadOnlyList<ShopSpecValueDto> Values);

/// <summary>前台规格值。</summary>
/// <param name="SpecValueId">规格值 Id。</param>
/// <param name="ValueName">规格值名。</param>
public sealed record ShopSpecValueDto(long SpecValueId, string ValueName);

/// <summary>前台 SKU。</summary>
/// <param name="SkuId">SKU Id。</param>
/// <param name="SkuName">SKU 名。</param>
/// <param name="SkuSpecText">规格文本。</param>
/// <param name="Image">SKU 图。</param>
/// <param name="OriginalPrice">原价。</param>
/// <param name="FinalPrice">到手价。</param>
/// <param name="DiscountSource">优惠来源标签。</param>
/// <param name="DiscountSourceName">优惠来源名称。</param>
/// <param name="SpecValueIds">该 SKU 覆盖的规格值 Id，前端用于按规格筛选。</param>
public sealed record ShopSkuDto(
    string SkuId, string SkuName, string SkuSpecText, string Image,
    decimal OriginalPrice, decimal FinalPrice,
    string DiscountSource, string DiscountSourceName, IReadOnlyList<long> SpecValueIds);

/// <summary>前台商品命令的校验器注册。</summary>
public static class ShopValidators
{
    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddShopValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<QueryShopProductsCommand>, QueryShopProductsValidator>();
        services.AddScoped<IValidator<QueryShopSearchCommand>, QueryShopSearchValidator>();
        services.AddScoped<IValidator<QueryShopProductDetailCommand>, QueryShopDetailValidator>();
    }

    /// <summary>前台列表查询校验。</summary>
    private sealed class QueryShopProductsValidator : AbstractValidator<QueryShopProductsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryShopProductsValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户 Id 不正确");
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须大于 0");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 50).WithMessage("每页条数必须在 1 ~ 50 之间");
            RuleFor(x => x.Keyword).MaximumLength(64).WithMessage("搜索关键字最多 64 个字符");
            RuleFor(x => x.SortBy).Must(a => a is >= ShopSorts.Default and <= ShopSorts.Newest)
                .WithMessage("排序方式不正确");
        }
    }

    /// <summary>前台搜索校验。</summary>
    private sealed class QueryShopSearchValidator : AbstractValidator<QueryShopSearchCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryShopSearchValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户 Id 不正确");
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须大于 0");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 50).WithMessage("每页条数必须在 1 ~ 50 之间");
            RuleFor(x => x.Keyword).MaximumLength(64).WithMessage("搜索关键字最多 64 个字符");
        }
    }

    /// <summary>前台详情查询校验。</summary>
    private sealed class QueryShopDetailValidator : AbstractValidator<QueryShopProductDetailCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryShopDetailValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户 Id 不正确");
            RuleFor(x => x.ProductId).GreaterThan(0).WithMessage("商品 Id 必须为正数");
        }
    }
}