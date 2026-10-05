using Collaboration.Domain.Common;
using MarketingService.Application.Features.Reports;
using MarketingService.Domain.Services;
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

    /// <summary>秒杀效果报表。</summary>
    /// <param name="command">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>逐场次的参与人数、抢购成功数、售罄率与成交额合计。</returns>
    /// <remarks>
    /// <b>不含「场次 PV」</b>：前台秒杀列表只有一个
    /// <c>POST /marketing/seckill/sessions/Public</c>，sessionId 在请求体里，
    /// 而 pv 日志只记录 path / queryString / method，拿不到请求体，
    /// 因此无法把页面访问按场次归因。与其给一个「所有场次都一样」的假数字，不如不提供。
    /// </remarks>
    [HttpPost("Seckill")]
    public Task<ApiResponse<SeckillReport>> Seckill(
        [FromBody] QuerySeckillReportCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
