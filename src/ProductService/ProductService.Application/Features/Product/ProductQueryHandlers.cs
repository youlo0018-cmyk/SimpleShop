using Collaboration.Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;
using ProductEnums = ProductService.Domain.Entities;
using ProductService.Application.Services;
using ProductService.Domain.IRepository;
using ProductEntity = ProductService.Domain.Entities.Product;
using ProductSpec = ProductService.Domain.Entities.ProductSpec;
using ProductSpecValue = ProductService.Domain.Entities.ProductSpecValue;
using Sku = ProductService.Domain.Entities.Sku;

namespace ProductService.Application.Features.Product;

/// <summary>审核商品处理器。</summary>
public sealed class ChangeProductAuditHandler : IRequestHandler<ChangeProductAuditCommand, ApiResponse>
{
    private readonly IProductRepository _products;
    private readonly IProductSearchIndex _search;
    private readonly ILogger<ChangeProductAuditHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="products">商品仓储。</param>
    /// <param name="search">商品搜索索引，审核结果要同步（审核通过的才能被搜到）。</param>
    /// <param name="logger">日志器。</param>
    public ChangeProductAuditHandler(
        IProductRepository products, IProductSearchIndex search, ILogger<ChangeProductAuditHandler> logger)
    {
        _products = products;
        _search = search;
        _logger = logger;
    }

    /// <summary>执行审核。</summary>
    /// <param name="request">审核命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(ChangeProductAuditCommand request, CancellationToken ct)
    {
        var product = await _products.GetByIdAsync(request.ProductId, ct);
        if (product is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "商品不存在");

        // 已审核通过的商品再审一次没有意义，直接拒绝，
        // 否则审核记录会变成一个可以反复刷的状态机。
        if (product.AuditStatus == ProductEnums.AuditStatuses.Approved)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, "该商品已审核通过，无需重复审核");
        }

        product.AuditStatus = request.AuditStatus;
        if (request.AuditStatus == ProductEnums.AuditStatuses.Rejected && !string.IsNullOrWhiteSpace(request.Reason))
        {
            // 驳回理由追加到备注里。不覆盖原备注：那里可能有运营自己写的说明。
            product.Remark = AppendReason(product.Remark, request.Reason);
        }

        await _products.UpdateAsync(product, ct);

        // 审核结果同步到索引：审核通过的才允许被前台搜到（ES 侧有 auditStatus 过滤）
        await SyncStatusAsync(product, ct);

        return ApiResponseFactory.Ok(request.AuditStatus == ProductEnums.AuditStatuses.Approved ? "审核已通过" : "已驳回");
    }

    /// <summary>同步商品审核 / 上下架状态到索引。失败只记日志。</summary>
    /// <param name="product">商品。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task SyncStatusAsync(ProductEntity product, CancellationToken ct)
    {
        try
        {
            await _search.UpdateStatusAsync(product.Id, product.AuditStatus, product.Status, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 索引失败不回错：审核是主链路，ES 只是加速。补偿任务会补齐。
            _logger.LogError(ex, "同步商品审核状态到索引失败：{ProductId}", product.Id);
        }
    }

    private static string AppendReason(string existing, string reason)
    {
        var text = $"[审核驳回] {reason}";
        return string.IsNullOrWhiteSpace(existing) ? text : $"{existing}\n{text}";
    }
}

/// <summary>提交审核处理器：把商品送回待审核。</summary>
/// <remarks>
/// 编辑被驳回的商品**不会**自动回到待审核，必须显式重新提交（DATA_SPEC 5.6）。
/// 否则运营改完就自动排队，审核员会看到一堆没改完就送审的商品。
/// </remarks>
public sealed class SubmitProductAuditHandler : IRequestHandler<SubmitProductAuditCommand, ApiResponse>
{
    private readonly IProductRepository _products;

    /// <summary>构造处理器。</summary>
    /// <param name="products">商品仓储。</param>
    public SubmitProductAuditHandler(IProductRepository products) => _products = products;

    /// <summary>执行提交。</summary>
    /// <param name="request">提交命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(SubmitProductAuditCommand request, CancellationToken ct)
    {
        var product = await _products.GetByIdAsync(request.ProductId, ct);
        if (product is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "商品不存在");

        if (product.AuditStatus == ProductEnums.AuditStatuses.Approved)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, "该商品已审核通过，无需重复提交");
        }

        if (product.AuditStatus == ProductEnums.AuditStatuses.Pending)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, "该商品已在审核队列中");
        }

        product.AuditStatus = ProductEnums.AuditStatuses.Pending;
        await _products.UpdateAsync(product, ct);
        return ApiResponseFactory.Ok("已提交审核");
    }
}

/// <summary>上下架处理器。</summary>
public sealed class ChangeProductListingHandler : IRequestHandler<ChangeProductListingCommand, ApiResponse>
{
    private readonly IProductRepository _products;
    private readonly IProductSearchIndex _search;
    private readonly ILogger<ChangeProductListingHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="products">商品仓储。</param>
    /// <param name="search">商品搜索索引，上下架要同步（下架的商品必须立刻搜不到）。</param>
    /// <param name="logger">日志器。</param>
    public ChangeProductListingHandler(
        IProductRepository products, IProductSearchIndex search, ILogger<ChangeProductListingHandler> logger)
    {
        _products = products;
        _search = search;
        _logger = logger;
    }

    /// <summary>执行上下架。</summary>
    /// <param name="request">上下架命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// 上架前置条件是审核已通过（DATA_SPEC 5.6）。下架没有前置条件——
    /// 商品有问题时必须能立刻下架，不能因为审核状态卡住。
    /// </remarks>
    public async Task<ApiResponse> Handle(ChangeProductListingCommand request, CancellationToken ct)
    {
        var product = await _products.GetByIdAsync(request.ProductId, ct);
        if (product is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "商品不存在");

        if (request.Status == ProductEnums.ListingStatuses.OnShelf && product.AuditStatus != ProductEnums.AuditStatuses.Approved)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, "审核未通过的商品不能上架");
        }

        product.Status = request.Status;
        await _products.UpdateAsync(product, ct);

        // 下架必须立刻从索引里消失：商品有问题时运营下架，用户却还能搜到、点进去才发现买不了，
        // 这比「搜不到」糟糕得多
        await SyncStatusAsync(product, ct);

        return ApiResponseFactory.Ok(request.Status == ProductEnums.ListingStatuses.OnShelf ? "已上架" : "已下架");
    }

    /// <summary>同步商品上下架状态到索引。失败只记日志。</summary>
    /// <param name="product">商品。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task SyncStatusAsync(ProductEntity product, CancellationToken ct)
    {
        try
        {
            await _search.UpdateStatusAsync(product.Id, product.AuditStatus, product.Status, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "同步商品上下架状态到索引失败：{ProductId}", product.Id);
        }
    }
}

/// <summary>删除商品处理器。</summary>
public sealed class DeleteProductHandler : IRequestHandler<DeleteProductCommand, ApiResponse>
{
    private readonly IProductRepository _products;
    private readonly IProductSearchIndex _search;
    private readonly ILogger<DeleteProductHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="products">商品仓储。</param>
    /// <param name="search">商品搜索索引，删除后要从索引里移除。</param>
    /// <param name="logger">日志器。</param>
    public DeleteProductHandler(
        IProductRepository products, IProductSearchIndex search, ILogger<DeleteProductHandler> logger)
    {
        _products = products;
        _search = search;
        _logger = logger;
    }

    /// <summary>执行删除。</summary>
    /// <param name="request">删除命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// 「有未完成订单禁止删除」这条规则要查 OrderService，而 OrderService 还没建。
    /// 这里先**不假装**实现了它，而是在文档里标明缺口——
    /// 等 OrderService 落地后补一个跨服务预检。写一个假的预检（永远返回「没有订单」）
    /// 比不写更危险：它看起来这条规则存在。
    /// </remarks>
    public async Task<ApiResponse> Handle(DeleteProductCommand request, CancellationToken ct)
    {
        var product = await _products.GetByIdAsync(request.ProductId, ct);
        if (product is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "商品不存在");

        await _products.DeleteProductAsync(product.Id, ct);

        // 从索引里移除：删掉的商品还能被搜到、点进去是空白，比搜不到更糟
        try
        {
            await _search.DeleteAsync(product.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "从商品索引移除失败：{ProductId}", product.Id);
        }

        return ApiResponseFactory.Ok("删除成功");
    }
}

/// <summary>商品详情处理器。</summary>
public sealed class QueryProductDetailHandler : IRequestHandler<QueryProductDetailCommand, ApiResponse<ProductDetailDto>>
{
    private readonly IProductRepository _products;

    /// <summary>构造处理器。</summary>
    /// <param name="products">商品仓储。</param>
    public QueryProductDetailHandler(IProductRepository products) => _products = products;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品详情，含规格与 SKU。</returns>
    public async Task<ApiResponse<ProductDetailDto>> Handle(QueryProductDetailCommand request, CancellationToken ct)
    {
        var product = await _products.GetByIdAsync(request.ProductId, ct);
        if (product is null)
        {
            return ApiResults.Fail<ProductDetailDto>(BaseApiResponseCode.NotFound, "商品不存在");
        }

        var specs = await _products.GetSpecsAsync(product.Id, ct);
        var values = await _products.GetSpecValuesAsync(product.Id, ct);
        var skus = await _products.GetSkusAsync(product.Id, ct);
        var links = await _products.GetSkuSpecLinksAsync(product.Id, ct);

        var linksBySku = links.GroupBy(a => a.SkuId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.SpecValueId).ToArray());

        var specDtos = specs
            .OrderBy(a => a.SortOrder)
            .Select(spec => new ProductSpecDto(
                spec.Id.ToString(),
                spec.SpecName,
                values.Where(a => a.SpecId == spec.Id)
                    .OrderBy(a => a.SortOrder)
                    .Select(a => new ProductSpecValueDto(a.Id.ToString(), a.ValueName))
                    .ToArray()))
            .ToArray();

        var skuDtos = skus
            .OrderBy(a => a.Id)
            .Select(sku => new SkuDto(
                sku.Id.ToString(),
                sku.SkuCode,
                sku.SkuName,
                sku.SkuSpecText,
                sku.Price,
                sku.OriginalPrice,
                sku.Image,
                sku.Status,
                linksBySku.TryGetValue(sku.Id, out var ids)
                    ? ids.Select(a => a.ToString()).ToArray()
                    : Array.Empty<string>()))
            .ToArray();

        var dto = new ProductDetailDto(
            product.Id.ToString(),
            product.SpuName,
            product.SubTitle,
            product.BrandId,
            product.BrandName,
            product.CategoryId,
            product.CategoryName,
            product.DeliveryType,
            product.MainImage,
            product.Images,
            product.DetailImages,
            product.OriginalPrice,
            product.MinPrice,
            product.MaxPrice,
            product.Description,
            product.AuditStatus,
            product.Status,
            product.Sales,
            product.EvaluationScore,
            product.EvaluationCount,
            specDtos,
            skuDtos);

        return ApiResults.Ok(dto);
    }
}

/// <summary>分页查询商品处理器。</summary>
public sealed class QueryProductsHandler : IRequestHandler<QueryProductsCommand, ApiResponse<List<ProductListItem>>>
{
    private readonly IProductRepository _products;

    /// <summary>构造处理器。</summary>
    /// <param name="products">商品仓储。</param>
    public QueryProductsHandler(IProductRepository products) => _products = products;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品列表。</returns>
    public async Task<ApiResponse<List<ProductListItem>>> Handle(QueryProductsCommand request, CancellationToken ct)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        // 把应用层的命令翻译成 Domain 的查询条件——仓储不认识 Command，也不该认识。
        var filter = new ProductQuery
        {
            Keyword = request.Keyword ?? string.Empty,
            CategoryId = request.CategoryId,
            BrandId = request.BrandId,
            Status = request.Status,
            AuditStatus = request.AuditStatus
        };

        var (rows, _) = await _products.QueryPagedAsync(page, pageSize, filter, ct);

        var list = rows.Select(a => new ProductListItem(
            a.Id.ToString(),
            a.SpuName,
            a.MainImage,
            a.BrandName,
            a.CategoryName,
            a.DeliveryType,
            a.MinPrice,
            a.MaxPrice,
            a.AuditStatus,
            a.Status,
            a.Sales)).ToList();

        return ApiResults.Ok(list);
    }
}
