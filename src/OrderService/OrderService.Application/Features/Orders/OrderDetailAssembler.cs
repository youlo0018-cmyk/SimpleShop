using OrderService.Domain.Entities;
using OrderService.Domain.Ports;
using OrderService.Domain.Services;

namespace OrderService.Application.Features.Orders;

/// <summary>订单详情组装器：C 端与后台共用同一份。</summary>
/// <remarks>
/// 为什么抽出来而不是各写一份：详情页的后台版与 C 端版字段完全相同，
/// 复制一份之后改一处忘一处，表现为「后台少显示优惠构成、C 端多显示一行」，
/// 运营会以为数据错了。两边共用一份就没有这个问题。
/// </remarks>
public static class OrderDetailAssembler
{
    /// <summary>用已查出的订单与订单行组装详情。</summary>
    /// <param name="order">订单实体。</param>
    /// <param name="items">订单行。</param>
    /// <returns>详情 DTO。</returns>
    public static OrderDetailDto Build(Order order, IReadOnlyCollection<OrderItem> items)
        => new(
            order.Id, order.OrderNo, order.Status, OrderStatusMachine.NameOf(order.Status),
            OrderStatusMachine.CanCancel(order.Status),
            OrderStatusMachine.CanConfirmReceipt(order.Status),
            order.GoodsTotal, order.Freight, order.PointsDeduction, order.PayableAmount,
            order.PointsUsed, order.CouponId, order.CouponDiscount,
            order.ReceiverName, order.ReceiverPhone, order.ReceiverAddress,
            order.Remark, order.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            items.Select(a => new OrderItemDto(
                a.Id, a.SkuId, a.SpuId, a.ProductName, a.SkuSpecText,
                a.Price, a.Quantity, a.OriginalAmount,
                a.ActivityDiscount, a.CouponDiscount, a.PayableAmount, a.DeliveryType,
                a.SourceType)).ToList(),
            order.LogisticsCompanyId,
            order.LogisticsCompanyName,
            order.TrackingNo,
            // 未发货时给空串而不是 null：DTO 里所有字符串字段都是非空约定，
            // 前端直接显示即可，少写一处 ?. 判断。
            order.ShippedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
            order.RefundedAmount,
            // 剩余可退给到分位就当 0：差 0.004 元还提示「还能退 0.00 元」很奇怪
            Math.Max(0m, decimal.Round(
                order.PayableAmount - order.RefundedAmount, 2, MidpointRounding.AwayFromZero)));
}
