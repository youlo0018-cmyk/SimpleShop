using Collaboration.Domain.Common;
using MarketingService.Application.Features.Reports;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace MarketingService.Api.Controllers;

/// <summary>营销与秒杀效果报表（BUSINESS.md 17）。</summary>
/// <remarks>
/// 权限点：<c>report:marketing</c>（路径 <c>/gateway/reports/Marketing</c>）。
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

    /// <summary>营销效果报表（券部分）。</summary>
    /// <param name="command">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>发放 / 领取 / 核销 / 核销率 / 折扣总额。</returns>
    [HttpPost("Marketing")]
    public Task<ApiResponse<CouponReport>> Marketing(
        [FromBody] QueryCouponReportCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
