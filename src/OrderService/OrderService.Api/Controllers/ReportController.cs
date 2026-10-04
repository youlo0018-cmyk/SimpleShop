using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using OrderService.Application.Features.Reports;

namespace OrderService.Api.Controllers;

/// <summary>工作台与经营报表（BUSINESS.md 17）。</summary>
/// <remarks>
/// 权限点：<c>dashboard:view</c>（路径 <c>/gateway/reports/Report</c>）。
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

    /// <summary>工作台经营报表。</summary>
    /// <param name="command">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成交额、订单数、客单价、退款与预警等指标。</returns>
    [HttpPost("Report")]
    public Task<ApiResponse<BusinessReport>> BusinessReport(
        [FromBody] QueryBusinessReportCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
