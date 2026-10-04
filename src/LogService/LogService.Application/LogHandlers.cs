using Collaboration.Domain.Messaging;
using Microsoft.Extensions.Logging;

namespace LogService.Application;

/// <summary>页面访问日志处理器（订阅 pv.log）。</summary>
public sealed class PvLogHandler : IEventHandler
{
    private readonly ILogIndexer _indexer;
    private readonly ILogger<PvLogHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="indexer">日志索引写入端口。</param>
    /// <param name="logger">日志器。</param>
    public PvLogHandler(ILogIndexer indexer, ILogger<PvLogHandler> logger)
    {
        _indexer = indexer;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SubscribedTopics { get; } = [EventTopics.PvLog];

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, CancellationToken ct)
    {
        var entry = Deserialize<PvLogEntry>(envelope);
        await _indexer.IndexPvAsync(entry, ct);
    }

    /// <summary>反序列化事件载荷。</summary>
    /// <typeparam name="T">载荷类型。</typeparam>
    /// <param name="envelope">事件信封。</param>
    /// <returns>载荷对象。</returns>
    /// <exception cref="InvalidOperationException">载荷解析不出来时抛出，交由消费循环丢死信。</exception>
    /// <remarks>
    /// <b>载荷直接就是 JSON 文本</b>，不需要 base64 解码。
    /// 曾经这里写着 <c>Convert.FromBase64String</c>，那是在发布端还用
    /// MessagePack+base64 时的写法；发布端改成 JSON 之后忘了同步，
    /// 结果是每一条日志事件都会在这里抛 FormatException、全量进死信队列。
    /// 现在发布端与消费端共用 <see cref="EventJson"/> 一个口径，这类漂移做不出来了。
    /// </remarks>
    internal static T Deserialize<T>(EventEnvelope envelope)
    {
        var value = EventJson.Deserialize<T>(envelope.Payload);
        return value ?? throw new InvalidOperationException(
            $"{envelope.EventType} 载荷解析失败或结果为空。载荷：{Preview(envelope.Payload)}");
    }

    /// <summary>取载荷预览，截断到 200 字符。</summary>
    /// <param name="payload">原始载荷。</param>
    /// <returns>预览文本。</returns>
    /// <remarks>
    /// 截断是必须的：解析失败时把整个载荷塞进异常消息，
    /// 一个几 MB 的载荷会把服务日志顶爆，真正的错误原因反而看不见了。
    /// </remarks>
    private static string Preview(string payload)
        => payload.Length <= 200 ? payload : payload[..200] + "…";
}

/// <summary>写操作日志处理器（订阅 operation.log）。</summary>
public sealed class OperationLogHandler : IEventHandler
{
    private readonly ILogIndexer _indexer;

    /// <summary>构造处理器。</summary>
    /// <param name="indexer">日志索引写入端口。</param>
    public OperationLogHandler(ILogIndexer indexer) => _indexer = indexer;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SubscribedTopics { get; } = [EventTopics.OperationLog];

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, CancellationToken ct)
    {
        var entry = PvLogHandler.Deserialize<OperationLogEntry>(envelope);
        await _indexer.IndexOperationAsync(entry, ct);
    }
}

/// <summary>异常日志处理器（订阅 exception.log）。</summary>
public sealed class ExceptionLogHandler : IEventHandler
{
    private readonly ILogIndexer _indexer;

    /// <summary>构造处理器。</summary>
    /// <param name="indexer">日志索引写入端口。</param>
    public ExceptionLogHandler(ILogIndexer indexer) => _indexer = indexer;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SubscribedTopics { get; } = [EventTopics.ExceptionLog];

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, CancellationToken ct)
    {
        var entry = PvLogHandler.Deserialize<ExceptionLogEntry>(envelope);
        await _indexer.IndexExceptionAsync(entry, ct);
    }
}

