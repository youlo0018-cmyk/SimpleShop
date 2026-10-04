namespace Collaboration.Domain.Messaging;

/// <summary>
/// 一条消息最终处理失败、即将进死信队列时的上下文。
/// </summary>
/// <remarks>
/// 为什么要有它：RabbitMQ 的死信队列只保留**原始消息体**，不带「为什么失败」。
/// 运维在后台看到一堆死信时，最需要的信息恰恰是失败原因——
/// 没有它就只能一条条捞出来手工反序列化，排查成本极高。
/// </remarks>
/// <param name="EventId">事件 Id，消费方据此定位与重放。</param>
/// <param name="EventType">事件类型（routing key）。</param>
/// <param name="OccurredAt">事件发生时间 UTC。</param>
/// <param name="QueueName">消费队列名。</param>
/// <param name="ErrorMessage">失败原因，取异常 Message。</param>
/// <param name="ErrorType">异常类型全名。</param>
/// <param name="StackTrace">异常调用栈。</param>
/// <param name="FailedAt">判定失败的时间 UTC。</param>
/// <param name="Attempts">本次累计处理次数。</param>
/// <param name="PayloadPreview">载荷预览，截断到 2000 字符。</param>
public sealed record DeadLetterContext(
    string EventId,
    string EventType,
    DateTime OccurredAt,
    string QueueName,
    string ErrorMessage,
    string ErrorType,
    string StackTrace,
    DateTime FailedAt,
    int Attempts,
    string PayloadPreview);

/// <summary>
/// 死信记录端口：消费循环在 nack 之前调用它，把失败原因落到可查询的地方。
/// </summary>
/// <remarks>
/// 做成端口而不是让消费循环直接依赖某个存储：<b>消费循环本身必须保持「最不可能出错」的代码</b>。
/// 它只做「取信封 → 调处理器 → ack/nack」三件事，任何存储细节漏进来都会让最关键的一段变脆。
/// </remarks>
public interface IDeadLetterNotifier
{
    /// <summary>记录一条死信。</summary>
    /// <param name="context">死信上下文。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>实现**不允许抛异常**：记录失败只写日志，不能反过来让 nack 走不成。</remarks>
    Task RecordAsync(DeadLetterContext context, CancellationToken ct = default);
}

/// <summary>默认的空实现：谁都没注册记录端时用它，不影响消费。</summary>
public sealed class NullDeadLetterNotifier : IDeadLetterNotifier
{
    /// <inheritdoc />
    public Task RecordAsync(DeadLetterContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}
