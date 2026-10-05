using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Application.Features.Admin;

namespace PaymentService.Api.Controllers;

/// <summary>后台支付单查询。控制器纯转发，不做业务、不做验证、不查库（CODING_STANDARD 2.1）。</summary>
/// <remarks>
/// 上游刻意是 <c>admin/payments</c> 而不是 <c>payments</c>：
/// C 端那一组 Create / Query 是下单链路自己调的，混在一个前缀下会让
/// 「哪些接口要登录」在网关权限配置里难以分辨。
/// </remarks>
[ApiController]
[Route("admin/payments")]
public sealed class AdminPaymentController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public AdminPaymentController(IMediator mediator) => _mediator = mediator;

    /// <summary>支付单分页。可按状态与支付单号 / 订单号筛选。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>支付单分页结果。</returns>
    /// <remarks>
    /// 之前支付单只能按订单号查单条（<c>payments/Query</c>），后台没有任何分页入口 ——
    /// 运营想看「今天有哪些单支付失败」都做不到。
    /// </remarks>
    [HttpPost("List")]
    public Task<ApiResponse<PagedResult<AdminPaymentDto>>> List(
        [FromBody] QueryAdminPaymentsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
