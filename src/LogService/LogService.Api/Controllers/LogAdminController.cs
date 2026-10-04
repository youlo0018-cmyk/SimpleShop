using Collaboration.Domain.Common;
using LogService.Application;
using LogService.Domain;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace LogService.Api.Controllers;

/// <summary>后台日志查询（pv / operation / exception / 死信）。</summary>
/// <remarks>
/// 权限点：<c>log:read</c>（DATA_SPEC 5.27 / seed-permissions.ps1 里映射到 <c>/gateway/logs/*</c>）。
/// 这里只挂路由标注，鉴权由网关统一拦截——控制器里再判一次会出现两套口径。
/// </remarks>
[ApiController]
[Route("logs")]
public sealed class LogAdminController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public LogAdminController(IMediator mediator) => _mediator = mediator;

    /// <summary>页面访问日志列表。</summary>
    /// <param name="command">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页日志。</returns>
    [HttpPost("Pv/List")]
    public Task<ApiResponse<LogPage>> PvList(
        [FromBody] QueryPvLogsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>写操作日志列表。</summary>
    /// <param name="command">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页日志。</returns>
    [HttpPost("Operation/List")]
    public Task<ApiResponse<LogPage>> OperationList(
        [FromBody] QueryOperationLogsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>异常日志列表。</summary>
    /// <param name="command">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页日志，不含完整调用栈。</returns>
    [HttpPost("Exception/List")]
    public Task<ApiResponse<LogPage>> ExceptionList(
        [FromBody] QueryExceptionLogsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>取某条异常的完整调用栈。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>调用栈全文。</returns>
    [HttpPost("Exception/Stack")]
    public Task<ApiResponse<string>> ExceptionStack(
        [FromBody] GetExceptionStackCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>死信列表。</summary>
    /// <param name="command">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页死信，含失败原因与已重放次数。</returns>
    [HttpPost("DeadLetter/List")]
    public Task<ApiResponse<DeadLetterPage>> DeadLetterList(
        [FromBody] QueryDeadLettersCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>重放一条死信。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// 只重放**指定的那一条**，不提供「一键全量重放」：
    /// 死信里混着「下游没配好」和「数据本身是脏的」两类，
    /// 全量重放会把后者也打一遍，故障排查时噪声远大于收益。
    /// </remarks>
    [HttpPost("DeadLetter/Replay")]
    public Task<ApiResponse> Replay(
        [FromBody] ReplayDeadLetterCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
