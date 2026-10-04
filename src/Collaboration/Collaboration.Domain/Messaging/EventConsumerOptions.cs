namespace Collaboration.Domain.Messaging;

/// <summary>消费者配置。</summary>
/// <remarks>
/// 放在 Domain 而不是 Web：死信重放器（LogService.Infrastructure）也要读
/// <see cref="DeadLetterQueueName"/> 才能算出该去哪个队列捞消息。
/// 留在 Web 里的话，Infrastructure 就得反过来引用 Web——
/// 而 Web 依赖 ASP.NET Core 宿主，那会让一个纯存储适配器拖上整个 Web 栈。
/// </remarks>
public sealed class EventConsumerOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "EventConsumer";

    /// <summary>消费队列名。**每个消费服务必须用不同的队列名**，否则会互相抢消息。</summary>
    public string QueueName { get; set; } = string.Empty;

    /// <summary>单条消息最多处理几次，超过就丢进死信队列。</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>预取条数。太大时单个消费者囤积消息、停止消费，太小时性能差。</summary>
    public ushort PrefetchCount { get; set; } = 20;

    /// <summary>死信队列名。为空时自动用「队列名 + .dlq」。</summary>
    public string DeadLetterQueueName { get; set; } = string.Empty;

    /// <summary>两次重试之间的等待秒数。0 表示立即重投。</summary>
    public int RetryDelaySeconds { get; set; } = 2;

    /// <summary>死信原因保留的最大字符数。防止超长堆栈把 ES 文档撑爆。</summary>
    public int ErrorMaxLength { get; set; } = 2000;
}
