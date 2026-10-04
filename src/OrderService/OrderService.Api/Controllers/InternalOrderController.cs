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

    /// <summary>按订单号取评价所需信息（供 EvaluateService 调用）。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单状态与它买过的 SPU / SKU 清单。</returns>
    /// <remarks>
    /// 评价是 SPU 级的（规格 14.1），所以返回的数据也按 SPU 聚合：
    /// 买了同一 SPU 的 3 个规格，评价服务据此只写 1 条首评 + 3 个 SKU 标记。
    /// </remarks>
    [HttpPost("for-evaluate")]
    public Task<ApiResponse<OrderForEvaluateDto>> ForEvaluate(
        [FromBody] QueryOrderForEvaluateCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>按订单号取支付 / 退款所需的订单信息（供 PaymentService 调用）。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单金额、状态与逐行实付。</returns>
    /// <remarks>
    /// <b>金额一律服务端反查</b>（规格 10.1），支付服务不接受客户端传入的金额——
    /// 客户端报多少不是关键，「这笔单子实际该付多少」才关键。
    /// </remarks>
    [HttpPost("for-payment")]
    public Task<ApiResponse<OrderForPaymentDto>> ForPayment(
        [FromBody] QueryOrderForPaymentCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>把订单标记为已退款（PaymentService 退款审批通过后调用）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// 幂等：订单已是「已退款」时直接返回成功。退款审批可能被重复调用
    /// （消息重试、运营多点一次），报错会让上游以为失败而反复重试。
    /// </remarks>
    [HttpPost("mark-refunded")]
    public Task<ApiResponse> MarkRefunded(
        [FromBody] MarkOrderRefundedCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>支付完成收尾（PaymentService 支付成功后调用）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// 支付成功的副作用有四步（库存确认 / 积分冻结转实扣 / 券核销 / 订单转已支付），
    /// 全部复用 <c>OrderPaymentCompleter</c>。支付服务只负责「钱收到了没有」，
    /// 剩下的订单侧副作用由订单服务自己完成——在支付服务里复制一份，迟早会和这里漂移。
    /// </remarks>
    [HttpPost("complete-payment")]
    public Task<ApiResponse> CompletePayment(
        [FromBody] CompletePaymentCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
