using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using PointService.Application.Features.Reports;

namespace PointService.Api.Controllers;

/// <summary>积分报表（BUSINESS.md 17）。</summary>
/// <remarks>
/// 权限点：<c>report:view</c>（路径 <c>/gateway/reports/Point</c>）。
/// 这里只挂路由标注，鉴权由网关统一拦截。
/// </remarks>
[ApiController]
[Route("reports")]
public sealed class ReportController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public ReportController(IMediator mediator) => _mediator = mediator;

    /// <summary>积分报表。</summary>
    /// <param name="command">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>发放 / 消耗 / 过期 / 冻结与当前总余额。</returns>
    [HttpPost("Point")]
    public Task<ApiResponse<PointReport>> Point(
        [FromBody] QueryPointReportCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
