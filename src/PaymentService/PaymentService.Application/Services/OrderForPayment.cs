namespace PaymentService.Application.Services;

/// <summary>支付 / 退款用的订单行。</summary>
/// <param name="OrderItemId">订单行 Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="ProductName">商品名快照。</param>
/// <param name="SkuSpecText">规格快照。</param>
/// <param name="Quantity">数量。</param>
/// <param name="DeliveryType">配送方式。</param>
/// <param name="PayableAmount">该行实付金额。</param>
public sealed record OrderItemForPayment(long OrderItemId, long SkuId, string ProductName, string SkuSpecText, int Quantity, int DeliveryType, decimal PayableAmount);

/// <summary>支付 / 退款用的订单信息。</summary>
/// <param name="OrderId">订单 Id。</param>
/// <param name="OrderNo">订单号。</param>
/// <param name="CustomerId">下单客户 Id。</param>
/// <param name="PlatformId">平台 Id。</param>
/// <param name="MerchantId">商户 Id。</param>
/// <param name="Status">订单状态。</param>
/// <param name="StatusName">订单状态中文名。</param>
/// <param name="Freight">运费。整单退含运费，部分退不退。</param>
/// <param name="PayableAmount">实付金额，含运费。</param>
/// <param name="PointsUsed">本单抵扣的积分数。</param>
/// <param name="CouponId">使用的券 Id。退款不退券。</param>
/// <param name="Items">订单行。</param>
public sealed record OrderForPayment(long OrderId, string OrderNo, long CustomerId, long PlatformId, long MerchantId, int Status, string StatusName, decimal Freight, decimal PayableAmount, long PointsUsed, long CouponId, IReadOnlyList<OrderItemForPayment> Items);
