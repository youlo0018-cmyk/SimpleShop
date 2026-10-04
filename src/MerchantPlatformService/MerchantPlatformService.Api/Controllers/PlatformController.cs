using Collaboration.Domain.Common;
using MediatR;
using MerchantPlatformService.Application.Features.Platform;
using Microsoft.AspNetCore.Mvc;

namespace MerchantPlatformService.Api.Controllers;

/// <summary>平台管理接口。</summary>
/// <remarks>
/// 权限点：<c>platform:create</c> / <c>platform:update</c>（DATA_SPEC 5.1）。
/// 这里只挂路由标注，鉴权由网关统一拦截，不在控制器里再判一次。
/// </remarks>
[ApiController]
[Route("platforms")]
public sealed class PlatformController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public PlatformController(IMediator mediator) => _mediator = mediator;

    /// <summary>创建平台。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>平台 Id。</returns>
    [HttpPost("Create")]
    public Task<ApiResponse<long>> Create(
        [FromBody] CreatePlatformCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>编辑平台。<b>平台编码只读，传什么都会被忽略。</b></summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Update")]
    public Task<ApiResponse> Update(
        [FromBody] UpdatePlatformCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>删除平台。<b>平台下有商户时禁止删除，只能停用。</b></summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Delete")]
    public Task<ApiResponse> Delete(
        [FromBody] DeletePlatformCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>平台列表。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>平台分页，含商户数与状态中文名。</returns>
    [HttpPost("List")]
    public Task<ApiResponse<PagedPlatformDtos>> List(
        [FromBody] QueryPlatformsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>平台下拉框数据。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>全部启用平台；<b>直接返回名称供下拉显示</b>，不让前端拿 Id 再去拼名字。</returns>
    [HttpGet("Options")]
    public async Task<ActionResult<ApiResponse<List<PlatformOptionDto>>>> Options(
        CancellationToken ct)
    {
        var result = await _mediator.Send(new QueryPlatformOptionsQuery(), ct);
        return Ok(result);
    }
}
