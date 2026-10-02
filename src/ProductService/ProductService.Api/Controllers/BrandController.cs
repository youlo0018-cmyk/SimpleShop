using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using ProductService.Application.Features.Brand;

namespace ProductService.Api.Controllers;

/// <summary>商品品牌。商品的 BrandId 是<b>选填项</b>，所以品牌表可以为空（DATA_SPEC 5.5）。</summary>
[ApiController]
[Route("brands")]
public sealed class BrandController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public BrandController(IMediator mediator) => _mediator = mediator;

    /// <summary>分页查询品牌。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数，1-100。</param>
    /// <param name="keyword">按品牌名模糊搜索。</param>
    /// <param name="includeDisabled">是否包含停用品牌。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>品牌列表。</returns>
    [HttpGet("List")]
    public Task<ApiResponse<List<BrandListItem>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string keyword = "",
        [FromQuery] bool includeDisabled = false,
        CancellationToken ct = default)
        => _mediator.Send(new QueryBrandsCommand(page, pageSize, keyword, includeDisabled), ct);

    /// <summary>新建品牌。品牌名全局唯一。</summary>
    /// <param name="command">新建命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新品牌 Id。</returns>
    [HttpPost("Create")]
    public Task<ApiResponse<long>> Create([FromBody] CreateBrandCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>编辑品牌。</summary>
    /// <param name="command">编辑命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Update")]
    public Task<ApiResponse> Update([FromBody] UpdateBrandCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>删除品牌。有商品时拒绝，请改为停用。</summary>
    /// <param name="command">删除命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Delete")]
    public Task<ApiResponse> Delete([FromBody] DeleteBrandCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}