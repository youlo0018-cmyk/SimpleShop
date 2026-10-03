namespace OrderService.Domain.Entities;

/// <summary>
/// 订单状态机（BUSINESS.md 7.1）。
/// </summary>
/// <remarks>
/// <para><b>为什么状态迁移要集中在这里</b>：如果每个 Handler 自己写
/// <c>if (status == 10) ... else return 报错</c>，那么「取消」和「发货」两处各写一遍，
/// 迟早有一处漏掉某个前置状态，最后变成「已取消的订单居然被发货了」。
/// 集中成一张可测试的迁移表，所有入口（客户取消、商户发货、支付回调、定时关单）
/// 都问同一张表，规则改一处就够。</para>
///
/// <para>迁移用<b>条件更新</b>执行（<c>WHERE status = 期望的原状态</c>），
/// 不是「先查再改」：两个请求同时点「取消」时，只有一个能把 10 改成 91，
/// 另一个受影响行数为 0，据此回「订单状态已变更」。</para>
/// </remarks>
public static class OrderStatusMachine
{
    /// <summary>取状态的中文名。</summary>
    /// <param name="status">状态码。</param>
    /// <returns>中文名；未知状态回「未知状态」而不是空串，前端不用再判空。</returns>
    public static string NameOf(int status) => status switch
    {
        OrderStatuses.PendingPayment => "待支付",
        OrderStatuses.PendingShipment => "待发货",
        OrderStatuses.PendingReceipt => "待收货",
        OrderStatuses.PendingPickup => "待取货",
        OrderStatuses.Completed => "已完成",
        OrderStatuses.Refunded => "已退款",
        OrderStatuses.Cancelled => "已取消",
        _ => "未知状态"
    };

    /// <summary>是否可以从某状态迁到另一状态。</summary>
    /// <param name="from">原状态。</param>
    /// <param name="to">目标状态。</param>
    /// <returns>允许返回 true。</returns>
    /// <remarks>
    /// 三条硬约束：
    /// <list type="bullet">
    /// <item>50 已完成、60 已退款、91 已取消都是<b>终态</b>，谁都不许再改。</item>
    /// <item>只有 10 待支付能取消（BUSINESS.md 7.1）。已发货之后再取消等于货已经在路上了。</item>
    /// <item>虚拟订单（delivery_type=2）<b>不允许退款</b>——用户明确要求这一条只针对虚拟订单；
    /// 实物订单在 10/20/30/40 都能退款，50 之后不能。</item>
    /// </list>
    /// </remarks>
    public static bool CanTransit(int from, int to) => (from, to) switch
    {
        // 待支付 → 待发货（支付成功，或实付 0 元直接跳过支付）/ 已取消
        (OrderStatuses.PendingPayment, OrderStatuses.PendingShipment) => true,
        (OrderStatuses.PendingPayment, OrderStatuses.Cancelled) => true,

        // 待发货 → 待收货（快递发货）/ 待取货（自提备货完成）
        (OrderStatuses.PendingShipment, OrderStatuses.PendingReceipt) => true,
        (OrderStatuses.PendingShipment, OrderStatuses.PendingPickup) => true,

        // 待收货 / 待取货 → 已完成
        (OrderStatuses.PendingReceipt, OrderStatuses.Completed) => true,
        (OrderStatuses.PendingPickup, OrderStatuses.Completed) => true,

        // 待支付 / 待发货 / 待收货 / 待取货 → 已退款（实物订单，用户确认收货后即 50，不能再退）
        (OrderStatuses.PendingPayment, OrderStatuses.Refunded) => true,
        (OrderStatuses.PendingShipment, OrderStatuses.Refunded) => true,
        (OrderStatuses.PendingReceipt, OrderStatuses.Refunded) => true,
        (OrderStatuses.PendingPickup, OrderStatuses.Refunded) => true,

        _ => false
    };

    /// <summary>是否是终态。</summary>
    /// <param name="status">状态码。</param>
    /// <returns>终态返回 true。</returns>
    public static bool IsTerminal(int status)
        => status is OrderStatuses.Completed or OrderStatuses.Refunded or OrderStatuses.Cancelled;

    /// <summary>订单当前是否还可以取消（只有 10）。</summary>
    /// <param name="status">状态码。</param>
    /// <returns>可以取消返回 true。</returns>
    public static bool CanCancel(int status) => CanTransit(status, OrderStatuses.Cancelled);

    /// <summary>订单当前是否可以由客户「确认收货」（只有 30 待收货）。</summary>
    /// <param name="status">状态码。</param>
    /// <returns>可以确认收货返回 true。</returns>
    /// <remarks>
    /// 这里刻意<b>不用</b> <see cref="CanTransit"/>：40 待取货也能迁到 50，
    /// 但那是<b>商户核销取货码</b>走的路，不是客户点「确认收货」。
    /// 两者混在一起会让自提订单在客户页面也冒出「确认收货」按钮。
    /// </remarks>
    public static bool CanConfirmReceipt(int status) => status == OrderStatuses.PendingReceipt;

    /// <summary>订单当前是否可以核销取货码（只有 40 待取货）。</summary>
    /// <param name="status">状态码。</param>
    /// <returns>可以核销返回 true。</returns>
    public static bool CanVerifyPickup(int status) => CanTransit(status, OrderStatuses.Completed);

    /// <summary>实物订单是否可退款。</summary>
    /// <param name="status">状态码。</param>
    /// <param name="deliveryTypes">订单里出现过的配送方式；含虚拟商品（2）就整单不可退。</param>
    /// <returns>可退款返回 true。</returns>
    /// <remarks>
    /// 含虚拟商品就整单不能退——这是用户定的规则（虚拟订单不退款）。
    /// 「部分退」在这里不成立：一张单里混了虚拟与实物时，
    /// 退实物不退款子会让积分、库存、券三条链路的分摊对不上。
    /// 要退就整单退，或者下单时就分开下。
    /// </remarks>
    public static bool CanRefund(int status, IEnumerable<int> deliveryTypes)
    {
        if (deliveryTypes.Any(a => a == DeliveryTypes.Virtual)) return false;
        return CanTransit(status, OrderStatuses.Refunded);
    }

    /// <summary>订单里出现过的配送方式是否含虚拟商品。</summary>
    /// <param name="deliveryTypes">配送方式集合。</param>
    /// <returns>含虚拟商品返回 true。</returns>
    public static bool IsVirtualOnly(IEnumerable<int> deliveryTypes)
    {
        var list = deliveryTypes.ToArray();
        return list.Length > 0 && list.All(a => a == DeliveryTypes.Virtual);
    }
}

/// <summary>配送方式（BUSINESS.md 8.3）。</summary>
public static class DeliveryTypes
{
    /// <summary>实物快递。</summary>
    public const int Express = 1;

    /// <summary>虚拟商品（发货即完成，不走物流）。</summary>
    public const int Virtual = 2;

    /// <summary>实物自提（备货完成后凭取货码核销）。</summary>
    public const int SelfPickup = 3;
}