using Collaboration.Domain.Common;
using EvaluateService.Application.Features.Evaluate;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace EvaluateService.Api.Controllers;

/// <summary>后台评价管理（隐藏 / 回复 / 列表）。</summary>
/// <remarks>
/// 权限点：<c>evaluate:read</c> 看列表、<c>evaluate:manage</c> 隐藏、
/// <c>evaluate:reply</c> 回复（DATA_SPEC 5.27）。
/// 这里只挂路由标注，鉴权由网关统一拦截——控制器里再判一次权限会出现两套口径。
/// </remarks>
[ApiController]
[Route("evaluates/admin")]
public sealed class EvaluateAdminController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public EvaluateAdminController(IMediator mediator) => _mediator = mediator;

    /// <summary>评价列表（后台）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>评价分页，含隐藏原因与真实昵称。</returns>
    /// <remarks>
    /// 后台**能看到匿名评价的真实昵称**（规格 14.2）：匿名只是对其他顾客隐藏，
    /// 平台要在出现纠纷时能追溯到人。
    /// </remarks>
    [HttpPost("List")]
    public Task<ApiResponse<EvaluatePageResult>> List(
        [FromBody] QueryAdminEvaluatesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>隐藏 / 恢复显示评价。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// 隐藏时必须填原因（2~200 字），后台要记审计。隐藏只是**打标记不删数据**，
    /// 客户在自己的评价列表里仍能看到「已被隐藏」。
    /// </remarks>
    [HttpPost("Hide")]
    public Task<ApiResponse> Hide([FromBody] HideEvaluateCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>回复评价。</summary>
    /// <param name="command">命令；AppendId 传 0 表示回复首评。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回复 Id。</returns>
    /// <remarks>
    /// 商户与平台**各只能回复 1 次**，且回复不可编辑只能追加。
    /// 「已经回过了」由唯一索引 <c>uk_evaluate_reply_once</c> 兜底。
    /// </remarks>
    [HttpPost("Reply")]
    public Task<ApiResponse<long>> Reply([FromBody] ReplyEvaluateCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
