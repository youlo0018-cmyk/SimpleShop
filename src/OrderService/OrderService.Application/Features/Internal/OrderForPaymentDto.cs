namespace OrderService.Application.Features.Internal;

/// <summary>支付 / 退款用的订单行。</summary>
/// <param name="OrderItemId">订单行 Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="ProductName">商品名快照。</param>
/// <param name="SkuSpecText">规格快照。</param>
/// <param name="Quantity">数量。</param>
/// <param name="DeliveryType">配送方式。</param>
/// <param name="PayableAmount">该行实付金额，退款时用它算该行可退余额。</param>
public sealed record OrderItemForPaymentDto(
    long OrderItemId, long SkuId, string ProductName, string SkuSpecText,
    int Quantity, int DeliveryType, decimal PayableAmount);

/// <summary>支付 / 退款用的订单信息。</summary>
/// <remarks>
/// 单独一个 DTO 而不是复用 OrderDetailDto：详情 DTO 面向 C 端展示，带收货人姓名电话地址，
/// 而支付服务只需要金额与状态。多传个人信息等于凭空扩大数据出库的边界。
/// </remarks>
/// <param name="OrderId">订单 Id。</param>
/// <param name="OrderNo">订单号。</param>
/// <param name="CustomerId">下单客户 Id。</param>
/// <param name="PlatformId">平台 Id。</param>
/// <param name="MerchantId">商户 Id。</param>
/// <param name="Status">订单状态。</param>
/// <param name="StatusName">订单状态中文名。</param>
/// <param name="GoodsTotal">商品总额。</param>
/// <param name="Freight">运费。</param>
/// <param name="PointsDeduction">积分抵扣金额。</param>
/// <param name="PayableAmount">实付金额，含运费。</param>
/// <param name="PointsUsed">本单抵扣的积分数，退款时按比例回收。</param>
/// <param name="CouponId">使用的券 Id。退款不退券，仅供后台展示。</param>
/// <param name="Items">订单行。</param>
public sealed record OrderForPaymentDto(
    long OrderId, string OrderNo, long CustomerId,
    long PlatformId, long MerchantId,
    int Status, string StatusName,
    decimal GoodsTotal, decimal Freight, decimal PointsDeduction, decimal PayableAmount,
    long PointsUsed, long CouponId,
    IReadOnlyList<OrderItemForPaymentDto> Items);
