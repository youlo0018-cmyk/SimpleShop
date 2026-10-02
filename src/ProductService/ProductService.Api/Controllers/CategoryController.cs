using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using ProductService.Application.Features.Category;

namespace ProductService.Api.Controllers;

/// <summary>商品分类。最多三级（DATA_SPEC 5.4）。</summary>
/// <remarks>
/// 注意 using：Features.Category 与 Domain.Entities.Category 同名，
/// 本控制器只用命令与 DTO，不引用实体，所以不会撞上那个遮蔽问题。
/// </remarks>
[ApiController]
[Route("categories")]
public sealed class CategoryController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public CategoryController(IMediator mediator) => _mediator = mediator;

    /// <summary>查询分类树。</summary>
    /// <param name="includeDisabled">是否包含停用分类，默认 false。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分类树，最多三级。</returns>
    [HttpGet("Tree")]
    public Task<ApiResponse<List<CategoryNodeDto>>> Tree([FromQuery] bool includeDisabled = false, CancellationToken ct = default)
        => _mediator.Send(new QueryCategoryTreeCommand(includeDisabled), ct);

    /// <summary>新建分类。Level 由服务端按上级链计算，前端传了也忽略。</summary>
    /// <param name="command">新建命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新分类 Id。</returns>
    [HttpPost("Create")]
    public Task<ApiResponse<long>> Create([FromBody] CreateCategoryCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>编辑分类。不允许改上级分类。</summary>
    /// <param name="command">编辑命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Update")]
    public Task<ApiResponse> Update([FromBody] UpdateCategoryCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>删除分类。有子分类或商品时拒绝，请改为停用。</summary>
    /// <param name="command">删除命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Delete")]
    public Task<ApiResponse> Delete([FromBody] DeleteCategoryCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}