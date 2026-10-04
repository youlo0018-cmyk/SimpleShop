using Collaboration.Domain.Common;
using MediatR;
using PaymentService.Application.Services;
using PaymentService.Domain.Entities;
using PaymentService.Domain.IRepository;
using PaymentService.Domain.Services;
using Microsoft.Extensions.Logging;

namespace PaymentService.Application.Features.Refund;

/// <summary>退款申请处理器（后台代客发起）。</summary>
public sealed class ApplyRefundHandler : IRequestHandler<ApplyRefundCommand, ApiResponse<long>>
{
    private readonly IRefundRepository _refunds;
    private readonly IOrderPort _orders;
    private readonly ILogger<ApplyRefundHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="refunds">退款仓储。</param>
    /// <param name="orders">订单服务端口。</param>
    /// <param name="logger">日志器。</param>
    public ApplyRefundHandler(IRefundRepository refunds, IOrderPort orders, ILogger<ApplyRefundHandler> logger)
    {
        _refunds = refunds;
        _orders = orders;
        _logger = logger;
    }

    /// <summary>执行申请。</summary>
    /// <param name="request">命令；Items 留空表示整单退。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>退款单 Id。</returns>
    public async Task<ApiResponse<long>> Handle(ApplyRefundCommand request, CancellationToken ct)
    {
        var orderNo = request.OrderNo.Trim();
        var order = await _orders.GetForPaymentAsync(orderNo, ct).ConfigureAwait(false);
        if (order is null)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.NotFound, "订单不存在或订单服务不可用");
        }

        // 配送方式取订单行的：虚拟与实物混在一单里时，按虚拟的严格窗口处理
        var deliveryTypes = order.Items.Select(a => a.DeliveryType).Distinct().ToList();
        var strictest = deliveryTypes.Contains(DeliveryTypeNumbers.Virtual)
            ? DeliveryTypeNumbers.Virtual
            : DeliveryTypeNumbers.Express;

        if (!RefundRules.CanRefund(order.Status, strictest))
        {
            return ApiResults.Fail<long>(
                BaseApiResponseCode.OrderStateInvalid,
                $"当前订单状态是「{order.StatusName}」，{RefundRules.WindowDescription(strictest)}");
        }

        var refundedItems = await _refunds.ListRefundedItemsAsync(orderNo, ct).ConfigureAwait(false);
        var alreadyRefunded = refundedItems.Sum(a => a.Amount);

        var (isWhole, total, itemAmounts, resolveError) = ResolveItems(request.Items, order, refundedItems);
        if (resolveError.Length > 0)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BusinessError, resolveError);
        }

        if (!RefundRules.WithinRefundableBalance(total, order.PayableAmount, alreadyRefunded))
        {
            return ApiResults.Fail<long>(
                BaseApiResponseCode.BusinessError,
                $"退款金额 {total:0.00} 元超过剩余可退 {(order.PayableAmount - alreadyRefunded):0.00} 元");
        }

        var refund = new RefundOrder
        {
            RefundNo = BuildRefundNo(),
            OrderId = order.OrderId,
            OrderNo = order.OrderNo,
            CustomerId = order.CustomerId,
            PlatformId = order.PlatformId,
            MerchantId = order.MerchantId,
            Amount = total,
            RefundType = isWhole ? RefundTypes.Whole : RefundTypes.Partial,
            Status = RefundStatuses.PendingApproval,
            Reason = request.Reason.Trim()
        };

        var items = new List<RefundOrderItem>();
        foreach (var (orderItemId, amount) in itemAmounts)
        {
            var line = order.Items.First(a => a.OrderItemId == orderItemId);
            items.Add(new RefundOrderItem
            {
                OrderItemId = line.OrderItemId,
                SkuId = line.SkuId,
                ProductName = line.ProductName,
                SkuSpecText = line.SkuSpecText,
                Quantity = line.Quantity,
                Amount = RefundRules.Round2(amount)
            });
        }

        var refundId = await _refunds.InsertAsync(refund, items, ct).ConfigureAwait(false);
        _logger.LogInformation("退款单 {RefundNo} 已创建，金额 {Amount}（{Type}）",
            refund.RefundNo, total, RefundTypes.NameOf(refund.RefundType));

        return ApiResults.Ok(refundId, "退款申请已提交，等待审批");
    }

    /// <summary>解析本次要退的行与金额。</summary>
    /// <param name="inputs">调用方传入的行；空表示整单退。</param>
    /// <param name="order">订单信息。</param>
    /// <param name="refundedItems">该订单已退款明细。</param>
    /// <returns>是否整单退、总金额、逐行金额、错误信息。出错时 Error 非空。</returns>
    private static (bool IsWhole, decimal Total, List<(long ItemId, decimal Amount)> ItemAmounts, string Error)
        ResolveItems(
            IReadOnlyList<RefundItemInput>? inputs,
            Services.OrderForPayment order,
            IReadOnlyList<RefundOrderItem> refundedItems)
    {
        if (inputs is null || inputs.Count == 0)
        {
            // 整单退：含运费（规格 10.2）。部分退才不退运费
            var all = order.Items.Select(a => (a.OrderItemId, Amount: a.PayableAmount)).ToList();
            var goodsTotal = all.Sum(a => a.Amount);
            var withFreight = RefundRules.Round2(goodsTotal + order.Freight);
            return (true, withFreight, all, string.Empty);
        }

        var result = new List<(long ItemId, decimal Amount)>();
        foreach (var input in inputs)
        {
            var line = order.Items.FirstOrDefault(a => a.OrderItemId == input.OrderItemId);
            if (line is null)
            {
                return (false, 0m, [], $"订单行 {input.OrderItemId} 不属于该订单");
            }

            var lineRefunded = refundedItems
                .Where(a => a.OrderItemId == input.OrderItemId)
                .Sum(a => a.Amount);

            if (!RefundRules.WithinLineBalance(input.Amount, line.PayableAmount, lineRefunded))
            {
                return (false, 0m, [],
                    $"「{line.ProductName}」本行可退余额不足，申请 {input.Amount:0.00} 元，" +
                    $"最多还能退 {(line.PayableAmount - lineRefunded):0.00} 元");
            }

            result.Add((input.OrderItemId, RefundRules.Round2(input.Amount)));
        }

        return (false, RefundRules.Round2(result.Sum(a => a.Amount)), result, string.Empty);
    }

    /// <summary>生成退款单号：时间戳 + 6 位随机数。</summary>
    /// <returns>退款单号。</returns>
    private static string BuildRefundNo()
        => $"RFD{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.Next(100000, 1000000)}";
}
