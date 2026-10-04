using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Application.Features.Refund;

namespace PaymentService.Api.Controllers;

/// <summary>退款接口（代客申请 + 审批）。</summary>
/// <remarks>
/// 退款是**先申请、再审批**的两段式：申请时不动订单、不回补库存，
/// 只有审批通过才真正生效。这样「运营误点申请」不会立刻造成资损。
/// </remarks>
[ApiController]
[Route("refunds")]
public sealed class RefundController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public RefundController(IMediator mediator) => _mediator = mediator;

    /// <summary>代客申请退款。</summary>
    /// <param name="command">命令；Items 留空表示整单退。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>退款单 Id。</returns>
    /// <remarks>
    /// 退款窗口按配送方式判定：虚拟商品仅「待发货 / 待收货」可退，签收后不可退（含部分退款）；
    /// 实物全程可退。
    /// </remarks>
    [HttpPost("Apply")]
    public Task<ApiResponse<long>> Apply(
        [FromBody] ApplyRefundCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>审批通过退款。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>审批时会<b>再校验一次</b>「累计退款 ≤ 实付」，防止申请到审批之间被别的单超额占用。</remarks>
    [HttpPost("Approve")]
    public Task<ApiResponse> Approve(
        [FromBody] ApproveRefundCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>审批拒绝退款。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>拒绝<b>无任何副作用</b>：订单、库存、积分都不动。</remarks>
    [HttpPost("Reject")]
    public Task<ApiResponse> Reject(
        [FromBody] RejectRefundCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>退款单列表。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>退款单分页。</returns>
    [HttpPost("List")]
    public Task<ApiResponse<PagedRefundDtos>> List(
        [FromBody] QueryRefundsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
