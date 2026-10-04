using Collaboration.Domain.Configuration;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Collaboration.Domain.Messaging;

/// <summary>事件发布端口。</summary>
public interface IEventPublisher
{
    /// <summary>发布一个事件。</summary>
    /// <param name="eventType">事件类型，取值见 <see cref="EventTopics"/>。</param>
    /// <param name="payload">业务载荷对象。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true；发送失败返回 false，<b>不抛异常</b>。</returns>
    Task<bool> PublishAsync<T>(string eventType, T payload, CancellationToken ct = default);
}

/// <summary>基于 RabbitMQ 的事件发布实现。</summary>
/// <remarks>
/// <para><b>连接按需建立并复用</b>：连接是重量级对象，每发一条消息新建一个会把 broker 打垮。</para>
/// <para><b>发送失败返回 false 而不抛异常</b>：发布事件是<b>旁路</b>，主流程不能因为日志服务挂了而失败。
/// 当前关键副作用（扣库存 / 扣积分 / 核销券）都由各服务的同步链路完成，事件只用于日志与扩展消费方。
/// <b>但若将来某个事件成为关键副作用的唯一通路，必须换成 Outbox</b>——先落库再由独立进程投递，
/// 不能靠「重试几次」。</para>
/// <para><b>连接断开后自动重建</b>：单例持有的连接会因 broker 重启而失效，
/// 缓存死连接会让服务重启前一直发不出消息。</para>
/// </remarks>
public sealed class EventBus : IEventPublisher, IDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<EventBus> _logger;
    private readonly object _sync = new();
    private IConnection? _connection;
    private IModel? _channel;
    private bool _disposed;

    /// <summary>构造事件总线。</summary>
    /// <param name="options">RabbitMQ 连接配置。</param>
    /// <param name="logger">日志器。</param>
    public EventBus(RabbitMqOptions options, ILogger<EventBus> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> PublishAsync<T>(string eventType, T payload, CancellationToken ct = default)
    {
        try
        {
            var eventId = Guid.NewGuid().ToString("N");

            var envelope = new EventEnvelope
            {
                EventId = eventId,
                EventType = eventType,
                OccurredAt = DateTime.UtcNow,
                SchemaVersion = 1,
                // 口径统一走 EventJson，不在这里另写一份 JsonSerializerOptions。
                // 载荷是 **JSON 文本**，消费端直接反序列化 envelope.Payload 即可，
                // 不要再做 base64 解码。
                Payload = EventJson.Serialize(payload)
            };

            var body = Encoding.UTF8.GetBytes(EventJson.Serialize(envelope));

            lock (_sync)
            {
                var channel = EnsureChannel();

                var properties = channel.CreateBasicProperties();
                properties.Persistent = true;

                // eventId 同时写进信封与消息头：信封是给业务读的，消息头是给**消费端数重试次数**读的。
                // 少了消息头这一份，消费端就只能拿 delivery tag 当 key，而它每次重投都会变，
                // 于是「最多重试 N 次」会退化成「无限重试」。
                //
                // Persistent = true：日志丢了可以补，支付成功这类事件丢了就补不回来了。
                // broker 重启时内存队列里的消息会一并消失。
                properties.Headers = new Dictionary<string, object>
                {
                    ["eventId"] = Encoding.UTF8.GetBytes(eventId),
                    ["eventType"] = Encoding.UTF8.GetBytes(eventType)
                };

                channel.BasicPublish(EventTopics.Exchange, eventType, properties, body);
            }

            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "发布事件 {EventType} 失败（不影响主流程）", eventType);
            Reset();
            return Task.FromResult(false);
        }
    }

    /// <summary>确保连接与信道可用。</summary>
    /// <returns>可用信道。</returns>
    private IModel EnsureChannel()
    {
        if (_channel is { IsOpen: true }) return _channel;

        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost
        };

        _connection ??= factory.CreateConnection();
        _channel = _connection.CreateModel();

        // topic 交换机 + routing key 区分事件：加新事件只要多一个 routing key，
        // 不用改交换机、也不用重建队列
        _channel.ExchangeDeclare(EventTopics.Exchange, ExchangeType.Topic, durable: true);
        _channel.ExchangeDeclare(EventTopics.DeadLetterExchange, ExchangeType.Topic, durable: true);

        return _channel;
    }

    /// <summary>丢弃失效的连接与信道，下次发送时重建。</summary>
    private void Reset()
    {
        try
        {
            _channel?.Dispose();
        }
        catch
        {
            // 已经在 Dispose 里了，这里不用再管
        }

        _channel = null;
        _connection?.Dispose();
        _connection = null;
    }

    /// <summary>释放连接。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
    }
}
