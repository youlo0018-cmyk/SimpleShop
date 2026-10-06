using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using ProductService.Domain.Entities;
using ProductService.Domain.IRepository;

namespace ProductService.Application.Features.Product;

/// <summary>商品下拉（DATA_SPEC 4.2）：只返回<b>已上架</b>商品。</summary>
/// <param name="Keyword">按商品名模糊搜索，可空。</param>
/// <param name="Limit">最多返回多少条，1-200。</param>
public record QueryProductOptionsCommand(string Keyword = "", int Limit = 200)
    : IRequest<ApiResponse<List<ProductOption>>>;

/// <summary>装修可选商品下拉（DATA_SPEC 4.2）：<b>审核通过 + 已上架</b>。</summary>
/// <param name="Keyword">按商品名模糊搜索，可空。</param>
/// <param name="Limit">最多返回多少条，1-200。</param>
public record QueryDesignableProductsCommand(string Keyword = "", int Limit = 200)
    : IRequest<ApiResponse<List<ProductOption>>>;

/// <summary>按 SPU 取 SKU 下拉（DATA_SPEC 4.2）：只返回<b>启用</b> SKU，随 SPU 联动。</summary>
/// <param name="SpuId">商品 Id。</param>
public record QueryProductSkusCommand(long SpuId) : IRequest<ApiResponse<List<SkuOption>>>;

/// <summary>商品下拉项。</summary>
/// <param name="Id">商品 Id，字符串下发。</param>
/// <param name="Name">商品名。</param>
/// <param name="DeliveryType">
/// 配送方式。**必须带**：发货表单按它动态渲染（快递填物流、虚拟与自提不填），
/// 前端选了商品却不知道配送方式，就只能再查一次详情。
/// </param>
/// <remarks>下拉项统一形状 <c>{ id, name }</c>（DATA_SPEC 4.7），按需追加字段。</remarks>
public sealed record ProductOption(string Id, string Name, int DeliveryType);

/// <summary>SKU 下拉项。</summary>
/// <param name="Id">SKU Id，字符串下发。</param>
/// <param name="Name">规格文本（如「红色 / M」）。</param>
/// <param name="Price">售价，两位小数。</param>
public sealed record SkuOption(string Id, string Name, decimal Price);

/// <summary>下拉查询的校验器注册。</summary>
public static class ProductOptionValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddProductOptionValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<QueryProductOptionsCommand>, QueryProductOptionsValidator>();
        services.AddScoped<IValidator<QueryDesignableProductsCommand>, QueryDesignableProductsValidator>();
        services.AddScoped<IValidator<QueryProductSkusCommand>, QueryProductSkusValidator>();
    }

    /// <summary>商品下拉校验。</summary>
    private sealed class QueryProductOptionsValidator : AbstractValidator<QueryProductOptionsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryProductOptionsValidator()
        {
            RuleFor(x => x.Keyword).MaximumLength(64).WithMessage("关键词最多 64 个字符");
            RuleFor(x => x.Limit).InclusiveBetween(1, 200).WithMessage("下拉条数需为 1-200");
        }
    }

    /// <summary>装修选品下拉校验。</summary>
    private sealed class QueryDesignableProductsValidator : AbstractValidator<QueryDesignableProductsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryDesignableProductsValidator()
        {
            RuleFor(x => x.Keyword).MaximumLength(64).WithMessage("关键词最多 64 个字符");
            RuleFor(x => x.Limit).InclusiveBetween(1, 200).WithMessage("下拉条数需为 1-200");
        }
    }

    /// <summary>SKU 下拉校验。</summary>
    private sealed class QueryProductSkusValidator : AbstractValidator<QueryProductSkusCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryProductSkusValidator()
            => RuleFor(x => x.SpuId).GreaterThan(0).WithMessage("商品 Id 不合法");
    }
}

/// <summary>商品下拉处理器（已上架）。</summary>
/// <remarks>
/// 与 <see cref="QueryProductsHandler"/> 共用仓储查询，只把筛选条件固定成
/// 「已上架」并投影成下拉项：让下拉去调完整列表接口，前端要自己过滤，
/// 迟早出现「下拉里能选到已下架商品」。
/// </remarks>
public sealed class QueryProductOptionsHandler
    : IRequestHandler<QueryProductOptionsCommand, ApiResponse<List<ProductOption>>>
{
    private readonly IProductRepository _products;

    /// <summary>构造处理器。</summary>
    /// <param name="products">商品仓储。</param>
    public QueryProductOptionsHandler(IProductRepository products) => _products = products;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已上架商品的下拉项。</returns>
    public Task<ApiResponse<List<ProductOption>>> Handle(
        QueryProductOptionsCommand request, CancellationToken ct)
        => ProductOptionQueries.QueryAsync(
            _products, request.Keyword, request.Limit,
            status: ListingStatuses.OnShelf, auditStatus: 0, ct);
}

/// <summary>装修可选商品处理器（审核通过 + 已上架）。</summary>
/// <remarks>
/// 后台上下文**不注入公开可见性过滤**（DATA_SPEC 3.2.1），所以装修页需要一个
/// 显式只给「审核通过 + 已上架」的接口 —— 否则装修能把没过审的商品摆到首页。
/// 保存时的兜底校验在 <c>/internal/products/check-for-design</c>，这里是选品入口。
/// </remarks>
public sealed class QueryDesignableProductsHandler
    : IRequestHandler<QueryDesignableProductsCommand, ApiResponse<List<ProductOption>>>
{
    private readonly IProductRepository _products;

    /// <summary>构造处理器。</summary>
    /// <param name="products">商品仓储。</param>
    public QueryDesignableProductsHandler(IProductRepository products) => _products = products;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可装修商品的下拉项。</returns>
    public Task<ApiResponse<List<ProductOption>>> Handle(
        QueryDesignableProductsCommand request, CancellationToken ct)
        => ProductOptionQueries.QueryAsync(
            _products, request.Keyword, request.Limit,
            status: ListingStatuses.OnShelf, auditStatus: AuditStatuses.Approved, ct);
}

/// <summary>按 SPU 取 SKU 下拉处理器。</summary>
public sealed class QueryProductSkusHandler
    : IRequestHandler<QueryProductSkusCommand, ApiResponse<List<SkuOption>>>
{
    private readonly IProductRepository _products;

    /// <summary>构造处理器。</summary>
    /// <param name="products">商品仓储。</param>
    public QueryProductSkusHandler(IProductRepository products) => _products = products;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>启用 SKU 的下拉项；商品不存在时返回 404。</returns>
    public async Task<ApiResponse<List<SkuOption>>> Handle(
        QueryProductSkusCommand request, CancellationToken ct)
    {
        // 先确认商品存在：否则「商品 Id 写错」与「商品下没有启用 SKU」都回空列表，
        // 前端只能提示「暂无规格」，而真正的原因是商品不存在。
        var product = await _products.GetByIdAsync(request.SpuId, ct).ConfigureAwait(false);
        if (product is null)
        {
            return ApiResults.Fail<List<SkuOption>>(BaseApiResponseCode.NotFound, "商品不存在");
        }

        var skus = await _products.GetSkusAsync(request.SpuId, ct).ConfigureAwait(false);

        var list = skus
            .Where(a => a.Status == SkuStatuses.Enabled)
            .Select(a => new SkuOption(
                a.Id.ToString(),
                string.IsNullOrWhiteSpace(a.SkuSpecText) ? a.SkuName : a.SkuSpecText,
                a.Price))
            .ToList();

        return ApiResults.Ok(list);
    }
}

/// <summary>下拉查询的共用实现。</summary>
internal static class ProductOptionQueries
{
    /// <summary>按固定筛选条件取商品下拉项。</summary>
    /// <param name="products">商品仓储。</param>
    /// <param name="keyword">关键词。</param>
    /// <param name="limit">条数上限。</param>
    /// <param name="status">上下架筛选，0 不限。</param>
    /// <param name="auditStatus">审核状态筛选，0 不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下拉项。</returns>
    public static async Task<ApiResponse<List<ProductOption>>> QueryAsync(
        IProductRepository products, string? keyword, int limit, int status, int auditStatus,
        CancellationToken ct)
    {
        var filter = new ProductQuery
        {
            Keyword = keyword ?? string.Empty,
            Status = status,
            AuditStatus = auditStatus
        };

        // 租户裁剪（本平台 / 本商户）由 AOP 过滤注入，下拉不需要自己传 tenant 参数
        var (rows, _) = await products.QueryPagedAsync(1, limit, filter, ct).ConfigureAwait(false);

        return ApiResults.Ok(rows
            .Select(a => new ProductOption(a.Id.ToString(), a.SpuName, a.DeliveryType))
            .ToList());
    }
}
