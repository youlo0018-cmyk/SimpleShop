using System.Text;
using Collaboration.Domain.Configuration;
using Collaboration.Domain.Messaging;
using LogService.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace LogService.Infrastructure;

/// <summary>从 RabbitMQ 死信队列里精确捞回一条消息并重新投递。</summary>
/// <remarks>
/// <para><b>为什么要「捞」而不是直接 requeue</b>：死信队列里可能同时躺着
/// 支付成功、下单这类关键事件。全量 requeue 等于把一批旧业务事件重新打一遍，
/// 副作用是重复扣库存、重复发积分——这不是重试，这是制造事故。</para>
///
/// <para><b>对不上的消息要「轮转到队尾」而不是 requeue</b>：
/// <c>BasicGet</c> 拿到消息后不 ack 它就消失了，必须塞回去；
/// 但 <c>requeue=true</c> 是放回<b>队头</b>，下一次 <c>BasicGet</c> 又是同一条，
/// 扫描会原地打转。正确做法是重发到死信交换机再 ack 原消息，让队头腾出来。</para>
/// </remarks>
public sealed class RabbitMqDeadLetterReplayer : IDeadLetterReplayer
{
    /// <summary>单次重放最多向后翻多少条消息。</summary>
    private const int MaxScan = 500;

    private readonly RabbitMqOptions _mq;
    private readonly EventConsumerOptions _consumer;
    private readonly ILogger<RabbitMqDeadLetterReplayer> _logger;

    /// <summary>构造重放器。</summary>
    /// <param name="mq">RabbitMQ 连接配置。</param>
    /// <param name="consumer">消费者配置，用于取死信队列名。</param>
    /// <param name="logger">日志器。</param>
    public RabbitMqDeadLetterReplayer(
        IOptions<RabbitMqOptions> mq,
        IOptions<EventConsumerOptions> consumer,
        ILogger<RabbitMqDeadLetterReplayer> logger)
    {
        _mq = mq.Value;
        _consumer = consumer.Value;
        _logger = logger;
    }

    /// <summary>死信队列名。</summary>
    private string DlqName
        => string.IsNullOrWhiteSpace(_consumer.DeadLetterQueueName)
            ? _consumer.QueueName + ".dlq"
            : _consumer.DeadLetterQueueName;

    /// <inheritdoc />
    /// <remarks>
    /// RabbitMQ.Client 6.x 的 <c>BasicGet</c> 与 <c>BasicPublish</c> 都是同步 API，
    /// 没有异步版本。所以这里是同步实现包一层 <see cref="Task.FromResult{TResult}"/>，
    /// 而不是 <c>GetAwaiter().GetResult()</c>——后者在已经是同步上下文里会多绕一圈。
    /// </remarks>
    public Task<bool> ReplayAsync(string eventId, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Task.FromResult(false);
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(_mq.Host))
            return Task.FromResult(false);

        var factory = new ConnectionFactory
        {
            HostName = _mq.Host,
            Port = _mq.Port,
            UserName = _mq.UserName,
            Password = _mq.Password,
            VirtualHost = _mq.VirtualHost
        };

        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();

        var dlq = DlqName;

        // 交换机必须先声明才能 publish：交换机不存在时 publish 会 channel error，
        // 而这里正是「消息刚被服务重启清空了交换机」最容易命中的场景。
        channel.ExchangeDeclare(EventTopics.Exchange, ExchangeType.Topic, durable: true);
        channel.ExchangeDeclare(EventTopics.DeadLetterExchange, ExchangeType.Topic, durable: true);

        for (var scanned = 0; scanned < MaxScan; scanned++)
        {
            if (ct.IsCancellationRequested) return Task.FromResult(false);

            var result = channel.BasicGet(dlq, autoAck: false);
            if (result is null)
            {
                _logger.LogWarning("重放 {EventId} 失败：死信队列 {Queue} 已空", eventId, dlq);
                return Task.FromResult(false);
            }

            var body = result.Body.ToArray();
            var eventType = ReadEventType(body);

            if (Matches(body, eventId))
            {
                var properties = channel.CreateBasicProperties();
                properties.Persistent = true;

                // 重放也必须带 eventId 头：消费端靠它数重试次数，
                // 少了它这条消息会被当成全新消息，重试上限对重放消息完全失效。
                properties.Headers = new Dictionary<string, object>
                {
                    ["eventId"] = Encoding.UTF8.GetBytes(eventId),
                    ["eventType"] = Encoding.UTF8.GetBytes(eventType),
                    ["replayed"] = true
                };

                channel.BasicAck(result.DeliveryTag, multiple: false);
                channel.BasicPublish(EventTopics.Exchange, eventType, properties, body);

                _logger.LogInformation(
                    "死信 {EventId}（{EventType}）已重放回 {Exchange}",
                    eventId, eventType, EventTopics.Exchange);

                return Task.FromResult(true);
            }

            // 🔴 对不上的消息必须**轮转到队尾**，不能 BasicNack(requeue: true)。
            // requeue=true 是放回**队头**：下一个 BasicGet 拿到的又是同一条，
            // 循环原地打转 500 次也扫不到后面的目标消息——
            // 死信队列里只要有一条不匹配的消息，重放就永远失败。
            //
            // 正确做法是「重发到死信交换机 → ack 掉原消息」：
            // 新消息进队尾，队头就腾出来了，扫描得以推进。
            // 先 publish 再 ack：中途崩溃的话消息最多重复一次，不会丢。
            var carried = channel.CreateBasicProperties();
            carried.Persistent = true;
            carried.Headers = result.BasicProperties.Headers;

            channel.BasicPublish(EventTopics.DeadLetterExchange, dlq, carried, body);
            channel.BasicAck(result.DeliveryTag, multiple: false);
        }

        _logger.LogWarning(
            "重放 {EventId} 失败：向后翻了 {Max} 条都没找到", eventId, MaxScan);
        return Task.FromResult(false);
    }

    /// <summary>判断消息是不是目标事件。</summary>
    /// <param name="body">消息体。</param>
    /// <param name="eventId">目标事件 Id。</param>
    /// <returns>匹配返回 true。</returns>
    /// <remarks>
    /// 比对的是<b>信封里的 eventId</b>，不是消息头的：消息头可能被运维用
    /// 管理台重放时抹掉，而信封是消息体的一部分，不会被外部工具改。
    /// </remarks>
    private static bool Matches(byte[] body, string eventId)
    {
        var envelope = EventJson.Deserialize<EventEnvelope>(Encoding.UTF8.GetString(body));
        return envelope is not null
            && string.Equals(envelope.EventId, eventId, StringComparison.Ordinal);
    }

    /// <summary>从信封里读事件类型。</summary>
    /// <param name="body">消息体。</param>
    /// <returns>事件类型；解析不出返回空串。</returns>
    private static string ReadEventType(byte[] body)
        => EventJson.Deserialize<EventEnvelope>(Encoding.UTF8.GetString(body))?.EventType ?? string.Empty;
}
