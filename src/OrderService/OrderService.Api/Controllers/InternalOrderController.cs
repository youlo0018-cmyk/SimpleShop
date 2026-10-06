using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using OrderService.Application.Features.Internal;
using OrderService.Application.Features.Orders;
using OrderService.Domain.Ports;

namespace OrderService.Api.Controllers;

/// <summary>订单内部接口，供 ScheduledService 等服务间调用。网关不路由 /internal 前缀。</summary>
[ApiController]
[Route("internal/orders")]
public sealed class InternalOrderController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IOrderStore _store;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    /// <param name="store">订单仓储。报表类查询直接读仓储，不走命令。</param>
    public InternalOrderController(IMediator mediator, IOrderStore store)
    {
        _mediator = mediator;
        _store = store;
    }

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

    /// <summary>批量判断哪些订单号**确实不存在**（孤儿预留对账用）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>存在与不存在的订单号。</returns>
    /// <remarks>
    /// 下单链路是「锁库存 → 建订单」。进程死在两步之间时，库存会永远锁着而订单不存在。
    /// 定时任务靠这个接口确认「确实没有订单」，才敢去释放那些库存——
    /// 判错一次就是<b>释放真实订单所占的库存</b>，直接超卖。
    /// </remarks>
    [HttpPost("batch-exists")]
    public Task<ApiResponse<BatchOrderExistsResult>> BatchExists(
        [FromBody] BatchOrderExistsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>按订单号集合汇总成交额（秒杀效果报表用）。</summary>
    /// <param name="query">订单号集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已支付（不含已取消 / 已退款）的实付合计。</returns>
    /// <remarks>
    /// 秒杀订单在营销服务只有「订单号」，金额在订单库，所以 GMV 必须由订单服务算。
    /// 口径与工作台 GMV 一致，详见 <see cref="IOrderStore.SumPayableByOrderNosAsync"/>。
    /// </remarks>
    [HttpPost("sum-payable")]
    public async Task<ApiResponse<PayableSumResult>> SumPayable(
        [FromBody] PayableSumQuery query, CancellationToken ct)
    {
        // 同时回「总额」与「逐单金额」：秒杀报表要按**场次**算 GMV，
        // 而订单号属于哪个场次只有营销服务知道 —— 只回总额的话它要么 N 次调用，要么只能给全场一个数。
        var byOrder = await _store.GetPayableByOrderNosAsync(query.OrderNos, ct).ConfigureAwait(false);
        var amount = Math.Round(byOrder.Values.Sum(), 2, MidpointRounding.AwayFromZero);

        return ApiResults.Ok(new PayableSumResult(
            amount,
            byOrder.Select(a => new OrderPayableItem(a.Key, a.Value)).ToList()));
    }
}

/// <summary>按订单号汇总成交额的请求。</summary>
/// <param name="OrderNos">订单号集合。</param>
public sealed record PayableSumQuery(IReadOnlyList<string> OrderNos);

/// <summary>按订单号汇总成交额的结果。</summary>
/// <param name="Amount">成交额合计（两位小数）。</param>
/// <param name="Items">逐单实付金额；口径与 <paramref name="Amount"/> 相同（排除待支付 / 已取消 / 已退款）。</param>
public sealed record PayableSumResult(decimal Amount, IReadOnlyList<OrderPayableItem> Items);

/// <summary>单个订单的实付金额。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="Amount">实付金额，两位小数。</param>
public sealed record OrderPayableItem(string OrderNo, decimal Amount);
