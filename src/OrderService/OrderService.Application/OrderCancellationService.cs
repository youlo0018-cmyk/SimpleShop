using Microsoft.Extensions.Logging;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;

namespace OrderService.Application;

/// <summary>取消订单：改状态 + 退掉三项占用（库存 / 积分 / 券）。</summary>
/// <remarks>
/// <para>C 端「取消」与 Scheduled 的「支付超时关单」是<b>同一件事</b>，只是触发方式不同。
/// 写成两套实现的话，迟早有一边的回滚漏掉某一项（比如只退库存忘了退券），
/// 而那种单会一直占着用户的券，直到他手动去客服投诉。</para>
///
/// <para><b>先改状态再退占用</b>，不是反过来：反过来会出现两个并发取消都读到「待支付」、
/// 都去退了一遍库存，把对方那单的货也退了。改状态走条件更新，只有一个请求能成功。</para>
///
/// <para>退占用失败<b>绝不能</b>反过来把「已取消」改成「取消失败」——状态已经改掉了，
/// 订单对用户就是已取消；这时再抛错只会让人以为还能付款。
/// 退不掉的资源记 Error 日志（带订单号），交给补偿任务兜底。</para>
/// </remarks>
public sealed class OrderCancellationService
{
    private readonly IOrderStore _store;
    private readonly IInventoryPort _inventory;
    private readonly IPointPort _points;
    private readonly ICouponPort _coupons;
    private readonly ILogger<OrderCancellationService> _logger;

    /// <summary>构造取消服务。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="inventory">库存端口。</param>
    /// <param name="points">积分端口。</param>
    /// <param name="coupons">券端口。</param>
    /// <param name="logger">日志器。</param>
    public OrderCancellationService(
        IOrderStore store, IInventoryPort inventory, IPointPort points,
        ICouponPort coupons, ILogger<OrderCancellationService> logger)
    {
        _store = store;
        _inventory = inventory;
        _points = points;
        _coupons = coupons;
        _logger = logger;
    }

    /// <summary>取消一张处于待支付（10）的订单。</summary>
    /// <param name="order">订单。</param>
    /// <param name="reason">取消原因，写进日志便于区分主动取消与超时关单。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>取消结果，说明是不是真的改掉了状态。</returns>
    public async Task<OrderCancelResult> CancelAsync(Order order, string reason, CancellationToken ct)
    {
        // 条件更新：只有把「待支付」改掉的那个请求会拿到 1。
        // 拿到 0 说明别人已经处理过了，这时候再去退占用会退掉别人那单的库存。
        var affected = await _store
            .TryTransitStatusAsync(order.Id, OrderStatuses.PendingPayment, OrderStatuses.Cancelled, ct)
            .ConfigureAwait(false);

        if (affected == 0)
        {
            return OrderCancelResult.NotChanged();
        }

        await ReleaseOccupationsAsync(order, ct).ConfigureAwait(false);

        _logger.LogInformation("订单 {OrderNo} 已取消（{Reason}）", order.OrderNo, reason);
        return OrderCancelResult.Succeeded();
    }

    /// <summary>把该单占用的库存 / 积分 / 券都退回去。单项失败只记日志。</summary>
    /// <param name="order">已取消的订单。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task ReleaseOccupationsAsync(Order order, CancellationToken ct)
    {
        var items = await _store.ListItemsAsync(order.Id, ct).ConfigureAwait(false);

        foreach (var item in items)
        {
            try
            {
                await _inventory.ReleaseAsync(item.SkuId, item.Quantity, $"{order.OrderNo}:{item.SkuId}", ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "取消订单回退库存失败：{OrderNo} SKU {SkuId}", order.OrderNo, item.SkuId);
            }
        }

        if (order.PointsUsed > 0)
        {
            try
            {
                await _points.UnfreezeAsync(order.CustomerId, order.OrderNo, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "取消订单解冻积分失败：{OrderNo}", order.OrderNo);
            }
        }

        if (order.CouponId > 0)
        {
            try
            {
                await _coupons.ReleaseAsync(order.CustomerId, order.OrderNo, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "取消订单回退券失败：{OrderNo}", order.OrderNo);
            }
        }
    }
}

/// <summary>取消结果。</summary>
/// <param name="Changed">是否真的把状态从待支付改成了已取消。</param>
public readonly record struct OrderCancelResult(bool Changed)
{
    /// <summary>状态已被别人改掉。</summary>
    public static OrderCancelResult NotChanged() => new(false);

    /// <summary>本次取消生效。</summary>
    /// <remarks>方法名不叫 <c>Changed</c>：那个名字已经被位置参数占住了，
    /// 同名会直接编译不过（CS0102），而且读起来也分不清是字段还是工厂。</remarks>
    public static OrderCancelResult Succeeded() => new(true);
}