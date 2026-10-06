using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;
using OrderService.Domain.Services;

namespace OrderService.Application.Features.Internal;

/// <summary>把订单标记为已退款（PaymentService 审批通过后调用）。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="RefundAmount">本次退款金额。</param>
/// <param name="Items">
/// 本次退款的行明细。<b>不能省</b>：订单侧的「行级可退余额」就是靠它算的
/// （后台订单详情与再次退款的累计校验都读这张表）。为空时按「整单退」处理。
/// </param>
public record MarkOrderRefundedCommand(
    string OrderNo, decimal RefundAmount, IReadOnlyList<MarkOrderRefundedItem>? Items = null)
    : IRequest<ApiResponse>;

/// <summary>退款的一行。</summary>
/// <param name="OrderItemId">订单行 Id。</param>
/// <param name="Quantity">本次退的件数。</param>
/// <param name="Amount">该行本次退款金额，两位小数。</param>
public record MarkOrderRefundedItem(long OrderItemId, int Quantity, decimal Amount);

/// <summary>命令校验器注册。</summary>
public static class MarkOrderRefundedValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddMarkOrderRefundedValidators(IServiceCollection services)
        => services.AddScoped<IValidator<MarkOrderRefundedCommand>, MarkOrderRefundedValidator>();

    /// <summary>校验规则。</summary>
    private sealed class MarkOrderRefundedValidator : AbstractValidator<MarkOrderRefundedCommand>
    {
        /// <summary>构造校验器。</summary>
        public MarkOrderRefundedValidator()
        {
            RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("缺少订单号");
            RuleFor(x => x.RefundAmount).GreaterThan(0m).WithMessage("退款金额必须大于 0");
        }
    }
}

/// <summary>标记订单已退款的处理器。</summary>
public sealed class MarkOrderRefundedHandler : IRequestHandler<MarkOrderRefundedCommand, ApiResponse>
{
    private readonly IOrderStore _store;
    private readonly IInventoryPort _inventory;
    private readonly IPointPort _points;
    private readonly ISeckillPort _seckill;
    private readonly ILogger<MarkOrderRefundedHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="store">订单存储端口。</param>
    /// <param name="inventory">库存端口（退款回补）。</param>
    /// <param name="points">积分端口（退款按比例回收）。</param>
    /// <param name="seckill">秒杀端口（秒杀单的货要还回秒杀池）。</param>
    /// <param name="logger">日志器。</param>
    public MarkOrderRefundedHandler(
        IOrderStore store,
        IInventoryPort inventory,
        IPointPort points,
        ISeckillPort seckill,
        ILogger<MarkOrderRefundedHandler> logger)
    {
        _store = store;
        _inventory = inventory;
        _points = points;
        _seckill = seckill;
        _logger = logger;
    }

    /// <summary>执行标记。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// <para><b>这里必须做库存回补与积分回收</b>。BUSINESS.md 10.2 写得很明确：
    /// 「审批通过 → 退款单已退款 → 发 payment.refunded → 订单转 60 +
    /// <b>库存回补</b> + <b>积分回收</b>」。</para>
    ///
    /// <para>🔴 之前这里**只改了状态**：两段式退款（/refunds/Apply + Approve，
    /// 也就是规格里那套带审批的正式流程）走完之后，货永久从库存里消失、
    /// 积分也不会还。实测：下单 2 件 → 支付 → 申请 → 审批通过，
    /// available 停在 98、deducted 停在 2，而订单已经显示「已退款」。
    /// 订单服务另有一条 /admin/orders/Refund 的单步退款做了回补，
    /// 但后台的退款对话框走的是两段式那条 —— 规则又一次被写在了两条路径里。</para>
    ///
    /// <para>回补用的是与单步退款相同的 bizNo（订单号 + SKU），库存侧按它幂等，
    /// 所以重试不会把货还两遍。</para>
    /// </remarks>
    public async Task<ApiResponse> Handle(MarkOrderRefundedCommand request, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);
        if (order is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "订单不存在");

        if (order.Status == OrderStatuses.Refunded)
        {
            // 幂等：已经退过直接成功。退款审批可能被重复调用（重试、运营多点一次），
            // 报错会让上游以为失败而反复重试
            return ApiResponseFactory.Ok("订单已是已退款状态");
        }

        var items = await _store.ListItemsAsync(order.Id, ct).ConfigureAwait(false);

        // 只有**未支付**（10）的单还占着 locked；支付成功后库存已从 locked 变成 deducted。
        // 两段式退款只可能发生在 20 及以后，所以这里基本都是 replenish，
        // 但仍然按状态判定，免得将来放宽窗口时踩到同一个坑。
        var lockedPhase = order.Status == OrderStatuses.PendingPayment;

        foreach (var item in items)
        {
            try
            {
                // 秒杀行不看 lockedPhase：它的货在发布场次时就划走了，
                // 与常规池的 locked / deducted 都没有关系，只能减 sold_count 还回秒杀池。
                if (item.SourceType == OrderSourceTypes.Seckill)
                {
                    await _seckill.ReleaseGrabAsync(
                        order.CustomerId, item.SkuId, item.Quantity, order.OrderNo, ct).ConfigureAwait(false);
                }
                else if (lockedPhase)
                {
                    await _inventory.ReleaseAsync(
                        item.SkuId, item.Quantity, $"{order.OrderNo}:{item.SkuId}", ct).ConfigureAwait(false);
                }
                else
                {
                    await _inventory.ReplenishAsync(
                        item.SkuId, item.Quantity, $"{order.OrderNo}:{item.SkuId}", ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // 回补失败**不反过来让退款失败**：钱已经退给客户了，
                // 这时抛错会让上游以为没退、于是重试，直接变成二次退款。
                // 记 Error 事后对账 —— 与单步退款用的是同一套取舍。
                _logger.LogError(ex,
                    "退款回补库存失败：订单 {OrderNo} SKU {SkuId} 数量 {Quantity}",
                    order.OrderNo, item.SkuId, item.Quantity);
            }
        }

        // 按比例回收已扣积分（BUSINESS.md 10.3）。整单退时比例是 1，等价于全额回收。
        if (order.PointsUsed > 0 && order.PayableAmount > 0)
        {
            var ratio = Math.Clamp(request.RefundAmount / order.PayableAmount, 0m, 1m);
            try
            {
                await _points.RecoverByRefundAsync(
                    order.CustomerId, order.OrderNo, ratio, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "退款已生效但积分回收失败：订单 {OrderNo} 比例 {Ratio}，需人工补回收",
                    order.OrderNo, ratio);
            }
        }

        // ---- 写订单侧的退款台账 ----
        // 🔴 缺了这一步，两本账就对不上：资金流水在支付服务的 refund_order，
        // 而订单侧的「行级可退余额」读的是 order_refund_item。
        // 实测：走两段式整单退款之后，后台订单详情仍然显示「可退 100.00」——
        // 运营看到的是一个已经退完的单还挂着全额可退，而再次退款的累计校验
        // 也读这张表，等于把上限校验建立在一张空表上。
        var refundItems = (request.Items ?? []).Where(a => a.Amount > 0m).ToList();
        if (refundItems.Count == 0)
        {
            // 没带明细就按整单退处理：每行按「行实付 − 已退」记一遍
            var alreadyRefunded = await _store.AggregateRefundedItemsAsync(order.Id, ct).ConfigureAwait(false);
            refundItems = items
                .Select(item =>
                {
                    alreadyRefunded.TryGetValue(item.Id, out var already);
                    var remaining = Math.Max(0m, item.PayableAmount - already.Amount);
                    return new MarkOrderRefundedItem(item.Id, item.Quantity, remaining);
                })
                .Where(a => a.Amount > 0m)
                .ToList();
        }

        // 只有**累计退满**才把订单转 60。
        // 部分退款把整单标成「已退款」的话，剩下的钱客户再也退不了 ——
        // BUSINESS 10.2 明确支持按行部分退，单步退款那条路径也是这么做的
        // （只退一行时订单保持原状态）。两段式这条以前无条件转 60，
        // 于是「退了一件」等于「整单作废」。
        // 累计已退金额取**退款单主表的金额合计**，不是行明细合计 ——
        // 整单退含运费，而行明细只记商品金额（规格 10.2：整单退含运费、部分退不退运费）。
        // 用行明细去比实付，一笔含运费的整单退（商品 51 + 运费 10 = 61）永远比不满 61，
        // 订单就永远转不成已退款。
        var priorRefunds = await _store.ListRefundsAsync(order.Id, ct).ConfigureAwait(false);
        var priorRefunded = priorRefunds.Sum(a => a.Refund.Amount);

        var fullyRefunded = OrderRefundRules.Round2(priorRefunded + request.RefundAmount) >= order.PayableAmount;

        if (refundItems.Count > 0)
        {
            var refundedTotal = refundItems.Sum(a => a.Amount);

            var ledger = new OrderRefund
            {
                RefundNo = $"PR{order.OrderNo}",
                OrderId = order.Id,
                OrderNo = order.OrderNo,
                PlatformId = order.PlatformId,
                MerchantId = order.MerchantId,
                CustomerId = order.CustomerId,
                Amount = OrderRefundRules.Round2(refundedTotal),
                RefundType = refundItems.Count == items.Count && items.All(a =>
                        refundItems.Any(b => b.OrderItemId == a.Id && b.Amount >= a.PayableAmount))
                    ? OrderRefundTypes.Whole
                    : OrderRefundTypes.Partial,
                // 累计退满才算整单退完。与单步退款那条路径同一个判据，
                // 免得两条路径对「退完没退完」给出不同答案。
                FullyRefunded = fullyRefunded,
                Reason = "支付服务审批通过的退款",
                OperatorId = 0,
                OperatorName = "payment-service",
            };

            var ledgerItems = refundItems
                .Select(a =>
                {
                    var line = items.First(b => b.Id == a.OrderItemId);
                    return new OrderRefundItem
                    {
                        OrderItemId = line.Id,
                        SkuId = line.SkuId,
                        ProductName = line.ProductName,
                        SkuSpecText = line.SkuSpecText,
                        Quantity = a.Quantity,
                        Amount = OrderRefundRules.Round2(a.Amount),
                    };
                })
                .ToList();

            await _store.SaveRefundAsync(ledger, ledgerItems, ct).ConfigureAwait(false);
        }

        if (!fullyRefunded)
        {
            _logger.LogInformation(
                "订单 {OrderNo} 部分退款 {Amount} 元已生效，订单保持 {Status}，剩余仍可退",
                order.OrderNo, request.RefundAmount, order.Status);

            return ApiResponseFactory.Ok("退款已生效（部分退款，订单仍可继续退）");
        }

        var affected = await _store.TryTransitStatusAsync(order.Id, order.Status, OrderStatuses.Refunded, ct).ConfigureAwait(false);
        if (affected == 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.OrderStateInvalid, "订单状态已变更，请刷新后重试");
        }

        return ApiResponseFactory.Ok("订单已标记为已退款");
    }
}
