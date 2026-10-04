using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Application.Features.Payment;

namespace PaymentService.Api.Controllers;

/// <summary>支付接口。</summary>
[ApiController]
[Route("payments")]
public sealed class PaymentController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public PaymentController(IMediator mediator) => _mediator = mediator;

    /// <summary>创建支付单。</summary>
    /// <param name="command">命令，只有订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>支付单，金额由服务端反查。</returns>
    /// <remarks>
    /// 命令里**没有金额字段**：金额一律服务端反查订单实付（规格 10.1）。
    /// 客户端报多少不是关键，「这笔单子实际该付多少」才关键。
    /// </remarks>
    [HttpPost("Create")]
    public Task<ApiResponse<PaymentDto>> Create(
        [FromBody] CreatePaymentCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>确认支付。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>支付单视图。</returns>
    /// <remarks>重复确认返回已支付，<b>不会重复触发</b>订单侧收尾（积分实扣 / 库存确认 / 券核销）。</remarks>
    [HttpPost("Confirm")]
    public Task<ApiResponse<PaymentDto>> Confirm(
        [FromBody] ConfirmPaymentCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>查支付单。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>支付单视图。</returns>
    [HttpPost("Query")]
    public Task<ApiResponse<PaymentDto>> Query(
        [FromBody] QueryPaymentCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>后台模拟支付（订单列表每行的按钮）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>支付单视图。</returns>
    /// <remarks>
    /// 受 AgileConfig <c>Payment:SimulateEnabled</c> 控制。失败时订单**保持 10 待支付**，
    /// 库存还锁着、积分还冻着、券还占着，用户可以重新发起。
    /// </remarks>
    [HttpPost("Simulate")]
    public Task<ApiResponse<PaymentDto>> Simulate(
        [FromBody] SimulatePaymentCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
