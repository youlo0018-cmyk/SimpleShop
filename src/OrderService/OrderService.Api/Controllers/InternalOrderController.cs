using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using OrderService.Application.Features.Internal;
using OrderService.Application.Features.Orders;

namespace OrderService.Api.Controllers;

/// <summary>订单内部接口，供 ScheduledService 等服务间调用。网关不路由 /internal 前缀。</summary>
[ApiController]
[Route("internal/orders")]
public sealed class InternalOrderController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public InternalOrderController(IMediator mediator) => _mediator = mediator;

    /// <summary>秒杀下单（供 MarketingService 调用）。</summary>
    /// <param name="command">秒杀下单命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回订单号与实付金额。</returns>
    /// <remarks>
    /// 🔴 与普通下单的唯一区别：<b>库存已在下单前被预扣走</b>。
    /// 秒杀的货在发布场次时就从常规库存划走了，这里再走一次「锁常规库存」
    /// 等于锁走第二份，秒杀直接超卖。
    /// </remarks>
    [HttpPost("seckill-create")]
    public Task<ApiResponse<OrderCreatedDto>> SeckillCreate(
        [FromBody] CreateSeckillOrderCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>扫描并关闭支付超时的订单（BUSINESS.md 7.3：超时 30 分钟、每 30 秒扫一次）。</summary>
    /// <param name="command">关单命令；不传 <c>OrderNo</c> 就是扫全量，传了只关这一张。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>扫描 / 关单 / 跳过张数与逐单结果。</returns>
    /// <remarks>
    /// <b>为什么扫描逻辑在订单服务而不是 Scheduled</b>：订单表只有订单服务能查，
    /// 「哪些单超时了」是业务规则；而「什么时候扫」才是基础设施。
    /// 两者混在一起会让定时任务直接连别人的库，破坏服务边界。
    /// </remarks>
    [HttpPost("close-timeout")]
    public Task<ApiResponse<CloseTimeoutResult>> CloseTimeout(
        [FromBody] CloseTimeoutOrdersCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}