namespace PaymentService.Domain.Services;

/// <summary>订单状态编号（与 OrderService 同一套，见 BUSINESS.md 7）。</summary>
/// <remarks>
/// 这里刻意<b>复制一份常量而不是引用 OrderService.Domain</b>：微服务之间不共享程序集，
/// 引用过去会让两个服务的数据库模型绑在一起，改订单状态要同时发两个服务。
/// 代价是这份常量要跟 OrderService 保持同步——所以每个常量都注明了对应业务含义。
/// </remarks>
public static class OrderStatusNumbers
{
    /// <summary>待支付。</summary>
    public const int PendingPayment = 10;

    /// <summary>待发货。</summary>
    public const int PendingShipment = 20;

    /// <summary>待收货。</summary>
    public const int PendingReceipt = 30;

    /// <summary>待取货。</summary>
    public const int PendingPickup = 40;

    /// <summary>已完成。</summary>
    public const int Completed = 50;

    /// <summary>已退款。</summary>
    public const int Refunded = 60;

    /// <summary>取中文名。</summary>
    /// <param name="status">状态值。</param>
    /// <returns>中文名，未知值返回「未知」。</returns>
    public static string NameOf(int status) => status switch
    {
        PendingPayment => "待支付",
        PendingShipment => "待发货",
        PendingReceipt => "待收货",
        PendingPickup => "待取货",
        Completed => "已完成",
        Refunded => "已退款",
        _ => "未知"
    };
}

/// <summary>配送方式编号（与 ProductService / OrderService 同一套）。</summary>
public static class DeliveryTypeNumbers
{
    /// <summary>实物快递。</summary>
    public const int Express = 1;

    /// <summary>虚拟商品。</summary>
    public const int Virtual = 2;

    /// <summary>实物自提。</summary>
    public const int SelfPickup = 3;
}
