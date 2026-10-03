using Collaboration.Domain.Common;
using EvaluateService.Application.Features.Internal;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace EvaluateService.Api.Controllers;

/// <summary>评价内部接口（供定时任务调用）。网关不路由 /internal 前缀。</summary>
[ApiController]
[Route("internal/evaluates")]
public sealed class InternalEvaluateController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public InternalEvaluateController(IMediator mediator) => _mediator = mediator;

    /// <summary>全量重算商品与店铺评分（ScheduledService 每日 03:00 调用）。</summary>
    /// <param name="command">命令；WriteBack 传 false 可只算不回写（排查用）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>重算结果，含店铺评分。</returns>
    /// <remarks>
    /// <b>全量重算而不是增量更新</b>：增量要处理「新增加一分」「隐藏减一分」
    /// 「删除减一分」三条路径，漏一条分数就永久漂移且不可逆。
    /// 全量重算幂等、可重跑，跑几次结果一样。
    /// 与积分过期任务（02:00）**错开一小时**，避免两个全表任务同时压数据库。
    /// </remarks>
    [HttpPost("ratings/recompute")]
    public Task<ApiResponse<RecomputeRatingsResult>> RecomputeRatings(
        [FromBody] RecomputeRatingsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
