namespace PaymentService.Domain.Services;

/// <summary>退款的纯规则：退款窗口与金额上限。</summary>
/// <remarks>
/// 放在 Domain 层是为了能被单元测试直接覆盖——这两条都是<strong>资损边界</strong>，
/// 写错一次就是赔钱：窗口放太宽会让虚拟商品签收后还能退；金额放太松会允许累计退款超过实付。
/// </remarks>
public static class RefundRules
{
    /// <summary>判断当前订单状态是否允许发起退款。</summary>
    /// <param name="orderStatus">订单状态。</param>
    /// <param name="deliveryType">配送方式。</param>
    /// <returns>允许返回 true。</returns>
    public static bool CanRefund(int orderStatus, int deliveryType)
    {
        if (IsAlwaysUnrefundable(orderStatus)) return false;
        if (deliveryType == DeliveryTypeNumbers.Virtual) return IsVirtualRefundable(orderStatus);
        return IsPhysicalRefundable(orderStatus);
    }

    /// <summary>与配送方式无关、一律不可退的状态。</summary>
    /// <param name="orderStatus">订单状态。</param>
    /// <returns>不可退返回 true。</returns>
    /// <remarks>未付款的订单没有钱可退；已整单退过的也不能再退一次。</remarks>
    private static bool IsAlwaysUnrefundable(int orderStatus)
        => orderStatus is OrderStatusNumbers.PendingPayment or OrderStatusNumbers.Refunded;

    /// <summary>虚拟商品可退的状态：仅待发货、待收货。</summary>
    /// <param name="orderStatus">订单状态。</param>
    /// <returns>可退返回 true。</returns>
    /// <remarks>
    /// <para><b>签收（50）后不可退款，含部分退款</b>（规格 10.2）。
    /// 虚拟商品交付即完成、没有物流可追溯，签收后再退等于「用完还退」。</para>
    /// <para>⚠️ 之前的实现是「虚拟商品订单<strong>完全不支持退款</strong>」，这比规格严格得多：
    /// 虚拟订单在支付后、商户发货前是停在 <b>20 待发货</b> 的，那段时间里买家连
    /// 「一直没发货」都无法反馈，只能干等。把窗口卡死等于把正常退款需求逼成投诉。</para>
    /// </remarks>
    private static bool IsVirtualRefundable(int orderStatus)
        => orderStatus is OrderStatusNumbers.PendingShipment or OrderStatusNumbers.PendingReceipt;

    /// <summary>实物可退的状态：待发货、待收货、待取货、已完成全程可退。</summary>
    /// <param name="orderStatus">订单状态。</param>
    /// <returns>可退返回 true。</returns>
    /// <remarks>
    /// 实物有物流链路可查证，签收后仍存在「少件 / 破损 / 与描述不符」这类真实争议，
    /// 所以全程允许（规格 10.2）。
    /// </remarks>
    private static bool IsPhysicalRefundable(int orderStatus)
        => orderStatus is OrderStatusNumbers.PendingShipment
            or OrderStatusNumbers.PendingReceipt
            or OrderStatusNumbers.PendingPickup
            or OrderStatusNumbers.Completed;

    /// <summary>退款窗口的中文说明，给接口提示与后台文案用。</summary>
    /// <param name="deliveryType">配送方式。</param>
    /// <returns>窗口说明。</returns>
    public static string WindowDescription(int deliveryType)
        => deliveryType == DeliveryTypeNumbers.Virtual
            ? "虚拟商品在「待发货」「待收货」阶段可退，签收后不可退款"
            : "实物订单在待发货、待收货、待取货、已完成各阶段均可退款";

    /// <summary>判断本次退款金额是否超过订单剩余可退余额。</summary>
    /// <param name="refundAmount">本次申请金额。</param>
    /// <param name="paidAmount">订单实付（含运费）。</param>
    /// <param name="alreadyRefunded">已退金额合计。</param>
    /// <returns>未超额返回 true。</returns>
    /// <remarks>
    /// 允许 0.01 的容差不是「放过一点钱」，而是规避累加误差：
    /// 两次部分退款各自除不尽时，末次很可能差一分钱而把客户挡在门外。
    /// </remarks>
    public static bool WithinRefundableBalance(decimal refundAmount, decimal paidAmount, decimal alreadyRefunded)
    {
        var remaining = Round2(paidAmount) - Round2(alreadyRefunded);
        return Round2(refundAmount) <= remaining + 0.01m;
    }

    /// <summary>判断某一行的退款金额是否超过该行可退余额。</summary>
    /// <param name="refundAmount">该行本次退款金额。</param>
    /// <param name="linePaidAmount">该行实付金额。</param>
    /// <param name="lineAlreadyRefunded">该行已退金额合计。</param>
    /// <returns>未超额返回 true。</returns>
    public static bool WithinLineBalance(decimal refundAmount, decimal linePaidAmount, decimal lineAlreadyRefunded)
    {
        var remaining = Round2(linePaidAmount) - Round2(lineAlreadyRefunded);
        return Round2(refundAmount) <= remaining + 0.01m;
    }

    /// <summary>金额舍入：两位小数、四舍五入（AwayFromZero）。</summary>
    /// <param name="value">原始金额。</param>
    /// <returns>两位小数的金额。</returns>
    public static decimal Round2(decimal value)
        => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
