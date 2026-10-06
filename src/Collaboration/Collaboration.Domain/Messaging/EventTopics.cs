namespace Collaboration.Domain.Messaging;

/// <summary>事件 Topic 常量（对应 BUSINESS.md 20.1）。</summary>
/// <remarks>
/// <b>常量齐全 ≠ 都在用</b>：当前只有 3 个日志 Topic 真的走 MQ，
/// `ProductChanged` 会被发布但**没有消费者**（ES 同步由 ProductService 自己同步完成），
/// 其余业务 Topic 只有常量、没有生产者 —— 业务链路走的是同步内部调用。
/// 细节与「改异步前先建消费者」的提醒见 BUSINESS.md 20.1。
/// </remarks>
public static class EventTopics
{
    /// <summary>交换机名。单一 topic 交换机，用 routing key 区分事件。</summary>
    public const string Exchange = "simpleshop.events";

    /// <summary>死信交换机名。</summary>
    public const string DeadLetterExchange = "simpleshop.events.dlx";

    /// <summary>支付成功（含实付 0 自动支付、幂等补发）。</summary>
    public const string PaymentSucceeded = "payment.succeeded";

    /// <summary>退款审批通过。</summary>
    public const string PaymentRefunded = "payment.refunded";

    /// <summary>签收 / 核销完成。</summary>
    public const string OrderCompleted = "order.completed";

    /// <summary>主动取消 + 超时关单。</summary>
    public const string OrderCancelled = "order.cancelled";

    /// <summary>商品创建。</summary>
    public const string ProductCreated = "product.created";

    /// <summary>商品新建 / 修改 / 上下架 / 改价。</summary>
    public const string ProductChanged = "product.changed";

    /// <summary>发表首评。</summary>
    public const string EvaluateCreated = "evaluate.created";

    /// <summary>抢购预扣成功。</summary>
    public const string SeckillOrderRequested = "seckill.order.requested";

    /// <summary>秒杀场次结束 / 手动中止。</summary>
    public const string SeckillSessionEnded = "seckill.session.ended";

    /// <summary>页面访问。</summary>
    public const string PvLog = "pv.log";

    /// <summary>写操作。</summary>
    public const string OperationLog = "operation.log";

    /// <summary>未处理异常。</summary>
    public const string ExceptionLog = "exception.log";
}
