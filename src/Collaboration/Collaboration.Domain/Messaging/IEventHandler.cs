namespace Collaboration.Domain.Messaging;

/// <summary>事件处理器。由各消费服务实现，一个类型可处理多个 topic。</summary>
public interface IEventHandler
{
    /// <summary>本处理器关心的 topic（routing key）。</summary>
    /// <returns>topic 集合。</returns>
    IReadOnlyCollection<string> SubscribedTopics { get; }

    /// <summary>处理一条事件。</summary>
    /// <param name="envelope">事件信封。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// <b>抛异常 = 这条消息失败</b>，消费循环会重试并最终丢进死信队列。
    /// 所以「处理不了」的情况要<b>抛异常</b>而不是静默返回——
    /// 静默返回等于 ack 掉，一条数据就永久丢了，日志里还什么都看不到。
    ///
    /// <para><b>幂等由处理器自己保证</b>：RabbitMQ 的 at-least-once 会重复投递，
    /// 框架层做不了「只处理一次」——那需要业务侧的幂等键（如订单号）。</para>
    /// </remarks>
    Task HandleAsync(EventEnvelope envelope, CancellationToken ct);
}
