using System.Text;
using System.Collections.Concurrent;
using Collaboration.Domain.Configuration;
using Collaboration.Domain.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Collaboration.Web.Messaging;

/// <summary>事件消费后台服务：声明队列 → 绑定 → 消费 → ack / 重试 / 死信。</summary>
/// <remarks>
/// <para><b>手动 ack</b>：处理成功才 ack。自动 ack 的话，消息一投递就被确认，
/// 处理器还没跑完就崩了的话消息就没了。</para>
///
/// <para><b>重试后 nack 到死信</b>：一条处理不了的消息（脏数据、下游挂了）不能无限重试
/// 拖住整个队列。所以第 N 次仍失败就 <c>nack(requeue: false)</c>，
/// 借 <c>x-dead-letter-exchange</c> 落到死信队列，等人工/重放入口处理。</para>
///
/// <para><b>重试时先 nack(requeue: true) 而不是就地阻塞重试</b>：就地重试会把
/// 一条坏消息的处理时间叠加到当前线程上，预取额度被占满时整条队列都停摆。
/// 退回给 broker 让它重投，才不会因为一条脏数据拖垮整个消费者。</para>
///
/// <para><b>在内存里数重试次数</b>而不是用 header：连接断开后重连会丢失计数，
/// 但那种情况下消息本身也会被 broker 重投，行为退化成「重新从头处理」，
/// 这是安全的（因为处理逻辑必须幂等）。</para>
/// </remarks>
public sealed class EventConsumerService : BackgroundService
{
    private readonly RabbitMqOptions _mq;
    private readonly EventConsumerOptions _opt;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeadLetterNotifier _deadLetter;
    private readonly ILogger<EventConsumerService> _logger;

    /// <summary>
    /// 事件 Id → 已尝试次数。
    /// </summary>
    /// <remarks>
    /// key 用<b>事件 Id</b>而不是 delivery tag：delivery tag 每次重投都会变，
    /// 用它当 key 的话每次都从 1 开始数，重试上限形同虚设。
    /// </remarks>
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    private IConnection? _connection;
    IModel? _channel;

    /// <summary>构造消费者服务。</summary>
    /// <param name="mqs">RabbitMQ 连接配置。</param>
    /// <param name="options">消费者配置。</param>
    /// <param name="scopeFactory">作用域工厂，每条消息用它开一个作用域取处理器。</param>
    /// <param name="deadLetter">死信记录端口，用于把失败原因落到可查询的位置。</param>
    /// <param name="logger">日志器。</param>
    /// <remarks>
    /// <b>刻意不注入 <c>IEnumerable&lt;IEventHandler&gt;</c></b>：本类是单例（后台服务），
    /// 而处理器多半注册成 Scoped。直接注入的话 DI 会在根作用域创建它们，
    /// 处理器连同整条依赖图被永久钉在这个后台服务上——将来处理器里加一个
    /// DbContext，就会变成「单例持有请求作用域对象」，进程退出时释放不掉，
    /// 连接也一直不还。改成每条消息开作用域就没这个问题。
    /// </remarks>
    public EventConsumerService(
        IOptions<RabbitMqOptions> mqs,
        IOptions<EventConsumerOptions> options,
        IServiceScopeFactory scopeFactory,
        IDeadLetterNotifier deadLetter,
        ILogger<EventConsumerService> logger)
    {
        _mq = mqs.Value;
        _opt = options.Value;
        _scopeFactory = scopeFactory;
        _deadLetter = deadLetter;
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
                // 连接断了，内存里的重试计数已经没有意义：
                // broker 会把未 ack 的消息原样重投，行为退化成「重新从头处理」，
                // 而处理逻辑必须幂等，所以清空是安全的。
                // 不清的话，一次网络抖动会让后面所有**新**消息都被当成「已经重试过」直接进死信。
                _attempts.Clear();
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

        // 订阅哪些 topic 是一次性的、跟消息无关，所以在这里开一个短作用域取一次就够。
        // 处理器集合是静态的（注册在 DI 里），每条消息重开作用域只是为了拿到实例。
        List<string> topics;
        using (var scope = _scopeFactory.CreateScope())
        {
            topics = scope.ServiceProvider
                .GetServices<IEventHandler>()
                .SelectMany(a => a.SubscribedTopics)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

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
            var envelope = EventJson.Deserialize<EventEnvelope>(Encoding.UTF8.GetString(ea.Body.Span));
            if (envelope is null)
            {
                // 反序列化失败说明消息本身就是坏的，重试一万次也一样，直接丢死信
                _logger.LogError("消息反序列化失败，直接丢弃到死信队列");
                channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            // 每条消息一个作用域：处理器的依赖用完就释放，
            // 不跨消息共享任何东西。
            using var scope = _scopeFactory.CreateScope();

            foreach (var h in scope.ServiceProvider.GetServices<IEventHandler>()
                         .Where(a => a.SubscribedTopics.Contains(envelope.EventType)))
            {
                await h.HandleAsync(envelope, ct);
            }

            // 处理成功才算数，把计数清掉。
            // 不清的话：同一个 EventId 因为别的原因再次失败时，会被算成「已经试过 N 次」而直接进死信。
            _attempts.TryRemove(envelope.EventId, out _);
            channel.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var attempts = NextAttempt(ea);
            if (attempts < Math.Max(1, _opt.MaxAttempts))
            {
                _logger.LogWarning(
                    ex, "处理事件 {EventType} 失败（第 {Attempt}/{Max} 次），稍后重投",
                    ea.RoutingKey, attempts, _opt.MaxAttempts);

                // 退避后再重投。立刻 requeue 会让一条消息在毫秒级连打 N 次，
                // 下游刚起来那点恢复时间全被浪费掉了，N 次会瞬间耗光。
                if (_opt.RetryDelaySeconds > 0)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_opt.RetryDelaySeconds), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: true);
                return;
            }

            _logger.LogError(
                ex, "处理事件 {EventType} 连续 {Attempts} 次失败，丢入死信队列", ea.RoutingKey, attempts);

            await RecordDeadLetterAsync(ea, ex, attempts, ct);

            // 死信必须落好「为什么失败」，再把消息 nack 掉。
            // 顺序反过来的话，nack 之后进程崩了就再也补不回来了。
            channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    /// <summary>累加并返回本条消息的处理次数。</summary>
    /// <param name="ea">投递事件。</param>
    /// <returns>包含本次在内的累计处理次数。</returns>
    private int NextAttempt(BasicDeliverEventArgs ea)
    {
        // key 优先用消息头里的 EventId；没有就退回 routing key + 消息体的哈希。
        // 直接拿消息体当 key 是错的：同一条业务日志被重放多次时，
        // 它们会被当成同一条，计数累加后直接进死信。
        var key = ReadEventId(ea)
            ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ea.Body.Span));

        return _attempts.AddOrUpdate(key, 1, (_, current) => current + 1);
    }

    /// <summary>读消息头里的事件 Id，读不到返回 null。</summary>
    /// <param name="ea">投递事件。</param>
    /// <returns>事件 Id 或 null。</returns>
    private static string? ReadEventId(BasicDeliverEventArgs ea)
    {
        var headers = ea.BasicProperties.Headers;
        if (headers is null) return null;

        if (!headers.TryGetValue("eventId", out var raw) || raw is null) return null;

        return raw switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => raw.ToString()
        };
    }

    /// <summary>把失败原因写进死信记录。</summary>
    /// <param name="ea">投递事件。</param>
    /// <param name="ex">异常。</param>
    /// <param name="attempts">已尝试次数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task RecordDeadLetterAsync(
        BasicDeliverEventArgs ea, Exception ex, int attempts, CancellationToken ct)
    {
        var envelopeId = ReadEventId(ea) ?? string.Empty;
        var eventType = ea.RoutingKey;
        var occurredAt = DateTime.UtcNow;

        // 能解析出信封就取它自己的 EventId / OccurredAt：这两个字段是「业务真相」，
        // 比投递事件里可能为空的字段可靠。
        // EventJson.Deserialize 本身吞掉解析异常并返回 null，所以这里不需要 try/catch；
        // 解析不出来就沿用上面已经取到的值，不因为取不到元数据而放弃记录失败原因。
        var parsed = EventJson.Deserialize<EventEnvelope>(Encoding.UTF8.GetString(ea.Body.Span));
        if (parsed is not null)
        {
            if (!string.IsNullOrWhiteSpace(parsed.EventId)) envelopeId = parsed.EventId;
            if (parsed.OccurredAt != default) occurredAt = parsed.OccurredAt;
            if (!string.IsNullOrWhiteSpace(parsed.EventType)) eventType = parsed.EventType;
        }

        var max = Math.Max(100, _opt.ErrorMaxLength);

        var context = new DeadLetterContext(
            envelopeId,
            eventType,
            occurredAt,
            _opt.QueueName,
            Truncate(ex.Message, max),
            Truncate(ex.GetType().FullName ?? ex.GetType().Name, 200),
            Truncate(ex.StackTrace ?? string.Empty, max * 4),
            DateTime.UtcNow,
            attempts,
            Truncate(Encoding.UTF8.GetString(ea.Body.Span), max));

        try
        {
            await _deadLetter.RecordAsync(context, ct);
        }
        catch (Exception recordEx)
        {
            // 记录端自己挂了**绝不能**影响 nack——消息必须离开队列。
            // 否则这里一抛，catch 块外就没有 nack，消息会一直挂着被无限重投。
            _logger.LogError(recordEx, "记录死信失败（不影响丢弃，消息仍会进死信队列）");
        }
    }

    /// <summary>截断过长文本。</summary>
    /// <param name="text">原文本。</param>
    /// <param name="max">最大长度。</param>
    /// <returns>截断后的文本，必要时追加省略标记。</returns>
    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…(已截断)";

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
