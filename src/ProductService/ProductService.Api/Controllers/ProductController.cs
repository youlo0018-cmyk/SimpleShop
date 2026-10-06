using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using ProductService.Application.Features.Product;

namespace ProductService.Api.Controllers;

/// <summary>商品（SPU）。新建与编辑走同一个 Save 入口（DATA_SPEC 5.6）。</summary>
[ApiController]
[Route("products")]
public sealed class ProductController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public ProductController(IMediator mediator) => _mediator = mediator;

    /// <summary>分页查询商品。</summary>
    /// <param name="page">页码。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="keyword">按商品名模糊搜索。</param>
    /// <param name="categoryId">按分类过滤。</param>
    /// <param name="brandId">按品牌过滤。</param>
    /// <param name="merchantId">按商户过滤，0 表示不限。</param>
    /// <param name="status">按上下架过滤，0 不限。</param>
    /// <param name="auditStatus">按审核状态过滤，0 不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品列表。</returns>
    [HttpGet("List")]
    public Task<ApiResponse<List<ProductListItem>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string keyword = "",
        [FromQuery] long categoryId = 0,
        [FromQuery] long brandId = 0,
        [FromQuery] long merchantId = 0,
        [FromQuery] int status = 0,
        [FromQuery] int auditStatus = 0,
        CancellationToken ct = default)
        => _mediator.Send(new QueryProductsCommand(
            Page: page,
            PageSize: pageSize,
            Keyword: keyword,
            CategoryId: categoryId,
            BrandId: brandId,
            MerchantId: merchantId,
            Status: status,
            AuditStatus: auditStatus), ct);

    /// <summary>商品详情。含规格、SKU 与解析后的规格值 Id。</summary>
    /// <param name="productId">商品 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品详情。</returns>
    [HttpGet("Detail")]
    public Task<ApiResponse<ProductDetailDto>> Detail([FromQuery] long productId, CancellationToken ct)
        => _mediator.Send(new QueryProductDetailCommand(productId), ct);

    /// <summary>商品下拉（DATA_SPEC 4.2）：只返回**已上架**商品。</summary>
    /// <param name="keyword">按商品名模糊搜索。</param>
    /// <param name="limit">最多返回多少条，1-200。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下拉项 <c>{ value, label, deliveryType }</c>。</returns>
    /// <remarks>
    /// 带 <c>deliveryType</c>：发货表单按它动态渲染（快递填物流、虚拟与自提不填），
    /// 前端选了商品却不知道配送方式，就只能再查一次详情。
    /// </remarks>
    [HttpGet("Options")]
    public Task<ApiResponse<List<ProductOption>>> Options(
        [FromQuery] string keyword = "",
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
        => _mediator.Send(new QueryProductOptionsCommand(keyword, limit), ct);

    /// <summary>装修可选商品下拉（DATA_SPEC 4.2）：**审核通过 + 已上架**。</summary>
    /// <param name="keyword">按商品名模糊搜索。</param>
    /// <param name="limit">最多返回多少条，1-200。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下拉项。</returns>
    /// <remarks>
    /// 后台上下文不注入公开可见性过滤（3.2.1），所以装修页必须走这个显式过滤的接口，
    /// 否则装修能把没过审的商品摆到首页。保存时的兜底在
    /// <c>/internal/products/check-for-design</c>。
    /// </remarks>
    [HttpGet("Designable")]
    public Task<ApiResponse<List<ProductOption>>> Designable(
        [FromQuery] string keyword = "",
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
        => _mediator.Send(new QueryDesignableProductsCommand(keyword, limit), ct);

    /// <summary>按 SPU 取 SKU 下拉（DATA_SPEC 4.2）：只返回启用 SKU，随 SPU 联动。</summary>
    /// <param name="spuId">商品 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下拉项 <c>{ value, label, price }</c>；商品不存在返回 404。</returns>
    /// <remarks>
    /// 用查询参数而不是 <c>/products/{spuId}/Skus</c>：网关 RBAC 只支持**结尾**通配
    /// （`/*`），路径中间的占位符没法映射权限点，会退化成「查不到映射 → 后台令牌被拒」。
    /// </remarks>
    [HttpGet("Skus")]
    public Task<ApiResponse<List<SkuOption>>> Skus(
        [FromQuery] long spuId, CancellationToken ct)
        => _mediator.Send(new QueryProductSkusCommand(spuId), ct);

    /// <summary>保存商品。ProductId 传 0 表示新建。</summary>
    /// <param name="command">保存命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回商品 Id。</returns>
    /// <remarks>
    /// 新建固定「待审核 + 默认下架」；编辑**不重置审核状态**，需另行调用 SubmitAudit。
    /// </remarks>
    [HttpPost("Save")]
    public Task<ApiResponse<long>> Save([FromBody] SaveProductCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>新建商品。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新商品 Id。</returns>
    /// <remarks>
    /// 字段与 <c>products/Save</c> 完全一致，但**只建不改**：
    /// 之所以单独开一个端点而不是让权限点都绑 Save，是因为「能建档」与「能改价」
    /// 得能分开授予——共用一个端点就只能二选一，要么建档权限附带改价能力，要么谁都建不了。
    /// 本命令里没有 ProductId 字段，所以不存在「传了个 Id 就改成别的商品」的可能。
    /// </remarks>
    [HttpPost("Create")]
    public Task<ApiResponse<long>> Create([FromBody] CreateProductCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>提交审核：把商品送回待审核队列。</summary>
    /// <param name="command">提交命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("SubmitAudit")]
    public Task<ApiResponse> SubmitAudit([FromBody] SubmitProductAuditCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>审核商品：通过或驳回。</summary>
    /// <param name="command">审核命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Audit")]
    public Task<ApiResponse> Audit([FromBody] ChangeProductAuditCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>上架 / 下架。上架要求审核已通过。</summary>
    /// <param name="command">上下架命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("ChangeListing")]
    public Task<ApiResponse> ChangeListing([FromBody] ChangeProductListingCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>删除商品。</summary>
    /// <param name="command">删除命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Delete")]
    public Task<ApiResponse> Delete([FromBody] DeleteProductCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
