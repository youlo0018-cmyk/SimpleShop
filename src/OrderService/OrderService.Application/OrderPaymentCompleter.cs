using Microsoft.Extensions.Logging;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;

namespace OrderService.Application;

/// <summary>支付成功后的收尾：扣库存 → 实扣积分 → 核销券 → 发满赠券 → 改状态。</summary>
/// <remarks>
/// <para><b>下游先做、状态最后改</b>。反过来的话，状态一旦改成 20 就算支付成功，
/// 而库存扣减失败了也没人再重试——用户拿到「已支付」却没货。
/// 现在这样即使中途失败，订单还停在 10（待支付），重试时因为每一步都幂等
/// （库存靠 <c>(bizNo, skuId, action)</c> 流水唯一、积分与券靠 bizNo 幂等）可以直接重跑，不会重复扣。</para>
///
/// <para>测试环境的「模拟支付」按钮与将来的真实支付回调走<b>同一个</b>方法。
/// 分成两条路就会出现「模拟能过、真支付走不通」这种事，而它只在真付钱时才暴露。</para>
/// </remarks>
public sealed class OrderPaymentCompleter
{
    private readonly IOrderStore _store;
    private readonly IInventoryPort _inventory;
    private readonly IPointPort _points;
    private readonly ICouponPort _coupons;
    private readonly ILogger<OrderPaymentCompleter> _logger;

    /// <summary>构造收尾服务。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="inventory">库存端口。</param>
    /// <param name="points">积分端口。</param>
    /// <param name="coupons">券端口。</param>
    /// <param name="logger">日志器。</param>
    public OrderPaymentCompleter(
        IOrderStore store, IInventoryPort inventory, IPointPort points,
        ICouponPort coupons, ILogger<OrderPaymentCompleter> logger)
    {
        _store = store;
        _inventory = inventory;
        _points = points;
        _coupons = coupons;
        _logger = logger;
    }

    /// <summary>完成一笔支付。</summary>
    /// <param name="order">订单。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成结果，含失败原因与失败步骤。</returns>
    public async Task<OrderPaymentOutcome> CompleteAsync(Order order, CancellationToken ct = default)
    {
        // 实付 0 元的单在**创建时**就跳过了支付、直接进了 20 待发货。
        // 但它照样冻结了积分、占用了券，那些占用仍然要在这里结清——
        // 绝不能因为「没有支付这一步」就把积分和券晾在那里：积分会一直冻着，
        // 券会一直占着，用户再也没法用这张券，而订单在他眼里已经是完成的了。
        var settledWithoutPayment = order.PayableAmount == 0m;

        // 已关闭的单**必须拒付**，不能当成「重复回调」放行。
        //
        // 超时关单与支付回调之间天然有竞态：用户在超时前最后一秒点了支付，
        // 钱扣了、单却已经是 91。关单时券 / 积分 / 库存都已经释放回去了，
        // 这里再「当成功返回」等于告诉上游钱已收到、订单会发货 ——
        // 而实际上单已作废。用户的钱进了黑洞，而且系统报告的是**成功**，
        // 没有任何东西会触发对账，只能等用户来投诉。
        //
        // 拒掉之后上游才能把这笔钱原路退回，这是唯一能救回这笔钱的路径。
        if (order.Status == OrderStatuses.Cancelled)
        {
            _logger.LogWarning(
                "拒绝支付已关闭的订单 {OrderNo}：该单已超时关单，占用已释放，需要走退款",
                order.OrderNo);

            return OrderPaymentOutcome.Fail(0, "订单已关闭（超时未支付），无法完成支付，请联系客服退款");
        }

        // 非 0 元单：状态已经不是 10，说明支付回调重复到达（网关重试、消息重投都会），
        // 直接当成功返回，绝不能再去扣一遍库存。
        if (!settledWithoutPayment && order.Status != OrderStatuses.PendingPayment)
        {
            return OrderPaymentOutcome.Ok(alreadyCompleted: true);
        }

        var items = await _store.ListItemsAsync(order.Id, ct).ConfigureAwait(false);

        // ---- ① 扣减库存：locked → deducted ----
        foreach (var item in items)
        {
            // 🔴 秒杀行跳过：它的库存在**发布场次时**就从常规池划走了，
            // 下单时也没锁（见 CreateOrderRequest.InventoryPreDeducted）。
            // 这里再扣一次 = 扣走第三份，秒杀直接超卖。
            if (item.SourceType == OrderSourceTypes.Seckill)
            {
                _logger.LogInformation("① 跳过秒杀行扣减库存：订单 {OrderNo} SKU {SkuId}", order.OrderNo, item.SkuId);
                continue;
            }

            try
            {
                // 幂等键与下单锁定共用同一个 bizNo，只靠 action 区分，
                // 所以「锁定 2 件、实付后扣减 2 件」这两条流水能各自幂等重放。
                await _inventory.DeductAsync(item.SkuId, item.Quantity, $"{order.OrderNo}:{item.SkuId}", ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "支付扣减库存失败：订单 {OrderNo} SKU {SkuId}", order.OrderNo, item.SkuId);
                return OrderPaymentOutcome.Fail(1, "扣减库存失败：" + ex.Message);
            }
        }

        // ---- ② 实扣积分 ----
        if (order.PointsUsed > 0)
        {
            try
            {
                await _points.ConsumeAsync(order.CustomerId, order.OrderNo, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "支付实扣积分失败：订单 {OrderNo}", order.OrderNo);
                return OrderPaymentOutcome.Fail(2, "实扣积分失败：" + ex.Message);
            }
        }

        // ---- ③ 核销券 ----
        if (order.CouponId > 0)
        {
            try
            {
                await _coupons.ConsumeAsync(order.CustomerId, order.OrderNo, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "支付核销券失败：订单 {OrderNo}", order.OrderNo);
                return OrderPaymentOutcome.Fail(3, "核销优惠券失败：" + ex.Message);
            }
        }

        // ---- ④ 发满赠券 ----
        // 无条件调用：本单有没有满赠只有营销服务知道（承诺是下单时按当时的活动写的）。
        // 放在改状态之前，失败了整笔支付可以重跑——记录仍是「待发放」，
        // 重跑时按状态幂等，不会重复发券。放在改状态之后的话，
        // 状态一变就再也不会有人来补发，用户的券就**永久消失**了。
        try
        {
            await _coupons.IssueGiftsAsync(order.OrderNo, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "支付发放满赠券失败：订单 {OrderNo}", order.OrderNo);
            return OrderPaymentOutcome.Fail(4, "发放满赠券失败：" + ex.Message);
        }

        // ---- ⑤ 改状态：10 待支付 → 20 待发货 ----
        // 实付 0 元的单创建时就已经是 20，没有状态可迁，直接收工。
        if (settledWithoutPayment)
        {
            _logger.LogInformation("订单 {OrderNo} 实付 0 元，已在创建时进入待发货，本次只结清积分与券", order.OrderNo);
            return OrderPaymentOutcome.Ok(alreadyCompleted: true);
        }

        var affected = await _store
            // paidAt 与状态在**同一条 UPDATE** 里写：拆成两步的话，
            // 两步之间进程死掉会留下「已支付但 paid_at 为 null」的订单，
            // 报表按支付时间统计时它会从所有区间里消失，GMV 凭空少一块。
            .TryTransitStatusAsync(
                order.Id, OrderStatuses.PendingPayment, OrderStatuses.PendingShipment,
                paidAt: DateTime.UtcNow, ct: ct)
            .ConfigureAwait(false);

        if (affected == 0)
        {
            // 有别人抢先改掉了状态（多半是重复的支付回调）。以现在的状态为准：
            // 只要不是 10，就说明支付这件事已经处理过了，当成功。
            var current = await _store.FindByOrderNoAsync(order.OrderNo, ct).ConfigureAwait(false);
            if (current is not null && current.Status != OrderStatuses.PendingPayment)
            {
                _logger.LogInformation("订单 {OrderNo} 支付被重复触发，当前状态 {Status}", order.OrderNo, current.Status);
                return OrderPaymentOutcome.Ok(alreadyCompleted: true);
            }

            return OrderPaymentOutcome.Fail(5, "订单状态已变更，请刷新后重试");
        }

        _logger.LogInformation("订单 {OrderNo} 支付完成，进入待发货", order.OrderNo);
        return OrderPaymentOutcome.Ok();
    }
}

/// <summary>支付收尾结果。</summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="AlreadyCompleted">是否因为重复回调而无需再处理。</param>
/// <param name="FailedStep">失败步骤 1~5（1 扣库存 / 2 实扣积分 / 3 核销券 / 4 发满赠券 / 5 改状态），0 表示成功。</param>
/// <param name="Error">失败原因。</param>
public readonly record struct OrderPaymentOutcome(
    bool Succeeded, bool AlreadyCompleted, int FailedStep, string Error)
{
    /// <summary>构造成功结果。</summary>
    /// <param name="alreadyCompleted">是否重复回调。</param>
    public static OrderPaymentOutcome Ok(bool alreadyCompleted = false)
        => new(true, alreadyCompleted, 0, string.Empty);

    /// <summary>构造失败结果。</summary>
    /// <param name="step">失败步骤。</param>
    /// <param name="error">失败原因。</param>
    public static OrderPaymentOutcome Fail(int step, string error)
        => new(false, false, step, error);
}
