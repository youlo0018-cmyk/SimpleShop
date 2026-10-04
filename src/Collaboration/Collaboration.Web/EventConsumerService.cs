using System.Text;
using System.Text.Json;
using Collaboration.Domain.Configuration;
using Collaboration.Domain.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Collaboration.Web.Messaging;

/// <summary>消费者配置。</summary>
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
}

/// <summary>事件消费后台服务：声明队列 → 绑定 → 消费 → ack / 重试 / 死信。</summary>
/// <remarks>
/// <para><b>手动 ack</b>：处理成功才 ack。自动 ack 的话，消息一投递就被确认，
/// 处理器还没跑完就崩了的话消息就没了。</para>
///
/// <para><b>重试后 nack 到死信</b>：一条处理不了的消息（脏数据、下游挂了）不能无限重试
/// 拖住整个队列。所以第 N 次仍失败就 <c>nack(requeue: false)</c>，
/// 借 <c>x-dead-letter-exchange</c> 落到死信队列，等人工/重放入口处理。</para>
///
/// <para><b>在内存里数重试次数</b>而不是用 header：连接断开后重连会丢失计数，
/// 但那种情况下消息本身也会被 broker 重投，行为退化成「重新从头处理」，
/// 这是安全的（因为处理逻辑必须幂等）。</para>
/// </remarks>
public sealed class EventConsumerService : BackgroundService
{
    private readonly RabbitMqOptions _mq;
    private readonly EventConsumerOptions _opt;
    private readonly IEnumerable<IEventHandler> _handlers;
    private readonly ILogger<EventConsumerService> _logger;
    private IConnection? _connection;
    IModel? _channel;

    /// <summary>构造消费者服务。</summary>
    /// <param name="mqs">RabbitMQ 连接配置。</param>
    /// <param name="options">消费者配置。</param>
    /// <param name="handlers">全部事件处理器。</param>
    /// <param name="logger">日志器。</param>
    public EventConsumerService(
        IOptions<RabbitMqOptions> mqs,
        IOptions<EventConsumerOptions> options,
        IEnumerable<IEventHandler> handlers,
        ILogger<EventConsumerService> logger)
    {
        _mq = mqs.Value;
        _opt = options.Value;
        _handlers = handlers;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_opt.QueueName) || string.IsNullOrWhiteSpace(_mq.Host))
        {
            _logger.LogWarning("未配置 EventConsumer:QueueName 或 RabbitMq:Host，事件消费不启动");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ConsumeOnce(stoppingToken);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "事件消费连接异常，5 秒后重连");
                SafeDispose();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>建立连接并开始消费。</summary>
    /// <param name="ct">取消令牌。</param>
    private void ConsumeOnce(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = _mq.Host,
            Port = _mq.Port,
            UserName = _mq.UserName,
            Password = _mq.Password,
            VirtualHost = _mq.VirtualHost
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.BasicQos(0, _opt.PrefetchCount, global: false);

        var dlq = string.IsNullOrWhiteSpace(_opt.DeadLetterQueueName)
            ? _opt.QueueName + ".dlq"
            : _opt.DeadLetterQueueName;

        _channel.ExchangeDeclare(EventTopics.Exchange, ExchangeType.Topic, durable: true);
        _channel.ExchangeDeclare(EventTopics.DeadLetterExchange, ExchangeType.Topic, durable: true);

        _channel.QueueDeclare(dlq, durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind(dlq, EventTopics.DeadLetterExchange, dlq);

        // x-dead-letter-* 让「处理不了的消息」有一条确定的去处，
        // 而不是卡在主队列里反复重投、挡住后面的消息
        var args = new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = EventTopics.DeadLetterExchange,
            ["x-dead-letter-routing-key"] = dlq
        };

        _channel.QueueDeclare(_opt.QueueName, durable: true, exclusive: false, autoDelete: false, args);

        // RabbitMQ.Client 6.x 是同步消费者 API：回调返回后才算这条消息处理完。
        // 所以这里用 GetAwaiter().GetResult() 把异步处理压成同步——
        // 直接 fire-and-forget 会让 ack 早于处理完成，崩溃时消息就丢了
        var consumer = new EventingBasicConsumer(_channel);
        consumer.Received += (_, e) =>
        {
            try
            {
                OnMessageAsync(e, ct).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "消费消息时出现未捕获异常");
            }
        };
        _channel.BasicConsume(_opt.QueueName, autoAck: false, consumer);

        var topics = _handlers.SelectMany(a => a.SubscribedTopics).Distinct().ToList();
        foreach (var topic in topics)
        {
            _channel.QueueBind(_opt.QueueName, EventTopics.Exchange, topic);
        }

        _logger.LogInformation(
            "事件消费已启动：队列 {Queue}，订阅 {Topics}", _opt.QueueName, string.Join('、', topics));
    }

    /// <summary>处理一条消息。</summary>
    /// <param name="ea">投递事件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task OnMessageAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var channel = _channel;
        if (channel is null) return;

        try
        {
            var envelope = JsonSerializer.Deserialize<EventEnvelope>(ea.Body.Span, JsonOpts);
            if (envelope is null)
            {
                // 反序列化失败说明消息本身就是坏的，重试也没用，直接丢死信
                _logger.LogError("消息反序列化失败，直接丢弃到死信队列");
                channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            foreach (var h in _handlers.Where(a => a.SubscribedTopics.Contains(envelope.EventType)))
            {
                await h.HandleAsync(envelope, ct);
            }

            channel.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "处理事件 {EventType} 失败，丢入死信队列", ea.RoutingKey);
            channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    /// <summary>JSON 序列化选项，与发布端保持一致。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>安全释放连接与信道。</summary>
    private void SafeDispose()
    {
        try
        {
            _channel?.Dispose();
        }
        catch
        {
            // 已经在释放路径里了
        }

        _channel = null;
        _connection?.Dispose();
        _connection = null;
    }

    /// <summary>停止时释放连接。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public override async Task StopAsync(CancellationToken ct)
    {
        SafeDispose();
        await base.StopAsync(ct);
    }
}
