using System.Net.Http.Json;
using System.Text.Json;
using Collaboration.Domain.Messaging;
using LogService.Application;
using LogService.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogService.Infrastructure;

/// <summary>把死信写进 Elasticsearch，并实现 <see cref="IDeadLetterNotifier"/>。</summary>
/// <remarks>
/// 文档 Id 用 <b>EventId</b> 而不是自动生成：同一个 EventId 反复失败时会覆盖成一条，
/// 而不是攒出一堆「同一条消息失败了很多次」的记录——后者在后台根本没法用。
/// </remarks>
public sealed class ElasticsearchDeadLetterRepository : IDeadLetterRepository, IDeadLetterNotifier
{
    private readonly HttpClient _http;
    private readonly LogIndexOptions _opt;
    private readonly ILogger<ElasticsearchDeadLetterRepository> _logger;
    private readonly SemaphoreSlim _ensureOnce = new(1, 1);
    private volatile bool _ensured;

    /// <summary>构造仓储。</summary>
    /// <param name="httpClientFactory">HttpClient 工厂，取具名的 ES 客户端。</param>
    /// <param name="options">索引配置。</param>
    /// <param name="logger">日志器。</param>
    /// <remarks>
    /// 🔴 这里必须注入 <see cref="IHttpClientFactory"/> 再自己 <c>CreateClient("es")</c>，
    /// **不能**让 DI 直接注入 <see cref="HttpClient"/>。
    /// 直接注入拿到的是一个「什么都没配」的 HttpClient——没有 BaseAddress、没有超时，
    /// 于是相对地址（索引名）会直接抛
    /// "URI must be an absolute URI or BaseAddress must be set"。
    /// 而 DI 校验**不会**发现这个问题（HttpClient 确实能解析出来），
    /// 表现是「死信一条都记不上」，且只在真出现死信时才暴露。
    /// </remarks>
    public ElasticsearchDeadLetterRepository(
        IHttpClientFactory httpClientFactory,
        IOptions<LogIndexOptions> options,
        ILogger<ElasticsearchDeadLetterRepository> logger)
    {
        _http = httpClientFactory.CreateClient("es");
        _opt = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RecordAsync(DeadLetterRecord record, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.Url)) return;
        if (string.IsNullOrWhiteSpace(record.EventId)) return;

        await EnsureIndexAsync(ct).ConfigureAwait(false);

        var response = await _http
            .PutAsJsonAsync(
                $"{_opt.DeadLetterIndex}/_doc/{Uri.EscapeDataString(record.EventId)}",
                record, EventJson.Options, ct)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        _logger.LogError(
            "写死信索引失败：HTTP {Code} {Body}", (int)response.StatusCode, body);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 这里<b>吞掉异常</b>是对的：调用方是消费循环，它正等着 nack 一条消息。
    /// 记录失败必须只留日志，不能把异常抛回消费循环——
    /// 抛出去的话消息会一直留在队列里被反复重投，把整个消费者卡死。
    /// </remarks>
    public async Task RecordAsync(DeadLetterContext context, CancellationToken ct = default)
    {
        try
        {
            // 🔴 重放次数**必须继承**已有记录的值。
            // 死信文档按 EventId 覆盖写，而「重放后又失败」会再次走到这里——
            // 如果这里把 ReplayCount 重置成 0，计数就永远停在 0，
            // 重放上限形同虚设，一条永远修不好的消息可以被无限重放。
            // 实测踩过：重放成功后 replayCount 显示 0，第 4 次重放照样放行。
            var existing = await GetAsync(context.EventId, ct).ConfigureAwait(false);

            await RecordAsync(
                ToRecord(context, existing?.ReplayCount ?? 0, existing?.LastReplayAt),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "记录死信 {EventId} 失败", context.EventId);
        }
    }

    /// <inheritdoc />
    public async Task<DeadLetterPage> PageAsync(
        string eventType, int page, int pageSize, CancellationToken ct = default)
    {
        var empty = new DeadLetterPage([], 0, page, pageSize);
        if (string.IsNullOrWhiteSpace(_opt.Url)) return empty;

        object query = string.IsNullOrWhiteSpace(eventType)
            ? new { match_all = new { } }
            : new { term = new Dictionary<string, string> { ["eventType"] = eventType } };

        var body = new Dictionary<string, object?>
        {
            ["from"] = (page - 1) * pageSize,
            ["size"] = pageSize,
            ["track_total_hits"] = true,
            ["query"] = query,
            ["sort"] = new object[]
            {
                new Dictionary<string, object> { ["failedAt"] = new { order = "desc" } }
            }
        };

        HttpResponseMessage response;
        try
        {
            response = await _http
                .PostAsJsonAsync($"{_opt.DeadLetterIndex}/_search", body, JsonOpts, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "查死信索引失败");
            return empty;
        }

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogError("查死信索引失败：HTTP {Code} {Body}", (int)response.StatusCode, text);
            return empty;
        }

        var json = await response.Content
            .ReadFromJsonAsync<JsonElement>(JsonOpts, ct).ConfigureAwait(false);

        var items = new List<DeadLetterRecord>();
        long total = 0;

        if (json.TryGetProperty("hits", out var hits))
        {
            if (hits.TryGetProperty("total", out var totalNode))
            {
                total = totalNode.ValueKind == JsonValueKind.Object
                    ? totalNode.TryGetProperty("value", out var v) ? v.GetInt64() : 0
                    : totalNode.GetInt64();
            }

            if (hits.TryGetProperty("hits", out var arr))
            {
                foreach (var hit in arr.EnumerateArray())
                {
                    if (!hit.TryGetProperty("_source", out var src)) continue;
                    items.Add(ReadRecord(src));
                }
            }
        }

        return new DeadLetterPage(items, total, page, pageSize);
    }

    /// <inheritdoc />
    public async Task<DeadLetterRecord?> GetAsync(string eventId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.Url)) return null;
        if (string.IsNullOrWhiteSpace(eventId)) return null;

        var response = await _http
            .GetAsync($"{_opt.DeadLetterIndex}/_doc/{Uri.EscapeDataString(eventId)}", ct)
            .ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content
            .ReadFromJsonAsync<JsonElement>(JsonOpts, ct).ConfigureAwait(false);

        return json.TryGetProperty("_source", out var src) ? ReadRecord(src) : null;
    }

    /// <inheritdoc />
    public async Task<bool> MarkReplayedAsync(string eventId, DateTime replayedAt, CancellationToken ct = default)
    {
        var record = await GetAsync(eventId, ct).ConfigureAwait(false);
        if (record is null) return false;

        var updated = record with
        {
            ReplayCount = record.ReplayCount + 1,
            LastReplayAt = replayedAt
        };

        var response = await _http
            .PutAsJsonAsync(
                $"{_opt.DeadLetterIndex}/_doc/{Uri.EscapeDataString(eventId)}",
                updated, EventJson.Options, ct)
            .ConfigureAwait(false);

        return response.IsSuccessStatusCode;
    }

    /// <summary>JSON 选项。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>把死信上下文转成记录。</summary>
    /// <param name="context">死信上下文。</param>
    /// <param name="carryReplayCount">沿用已记录的重放次数。</param>
    /// <param name="carryLastReplayAt">沿用已记录的上次重放时间。</param>
    /// <returns>死信记录，重放次数与上次重放时间沿用传入值。</returns>
    private static DeadLetterRecord ToRecord(
        DeadLetterContext context, int carryReplayCount, DateTime? carryLastReplayAt)
        => new(
            context.EventId,
            context.EventType,
            context.OccurredAt,
            context.QueueName,
            context.ErrorMessage,
            context.ErrorType,
            context.StackTrace,
            context.FailedAt,
            context.Attempts,
            context.PayloadPreview,
            carryReplayCount,
            carryLastReplayAt);

    /// <summary>读一条死信记录。</summary>
    /// <param name="source">_source 节点。</param>
    /// <returns>死信记录。</returns>
    private static DeadLetterRecord ReadRecord(JsonElement source)
        => new(
            Str(source, "eventId"),
            Str(source, "eventType"),
            Date(source, "occurredAt"),
            Str(source, "queueName"),
            Str(source, "errorMessage"),
            Str(source, "errorType"),
            Str(source, "stackTrace"),
            Date(source, "failedAt"),
            Num(source, "attempts"),
            Str(source, "payloadPreview"),
            Num(source, "replayCount"),
            NullableDate(source, "lastReplayAt"));

    /// <summary>读字符串。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>字符串值。</returns>
    private static string Str(JsonElement node, string name)
        => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>读整型。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>整型值。</returns>
    private static int Num(JsonElement node, string name)
        => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : 0;

    /// <summary>读时间。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>UTC 时间。</returns>
    private static DateTime Date(JsonElement node, string name)
        => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && v.TryGetDateTime(out var dt)
                ? dt.ToUniversalTime()
                : default;

    /// <summary>读可空时间。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>UTC 时间或 null。</returns>
    private static DateTime? NullableDate(JsonElement node, string name)
        => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && v.TryGetDateTime(out var dt)
                ? dt.ToUniversalTime()
                : null;

    /// <summary>确保死信索引存在。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task EnsureIndexAsync(CancellationToken ct)
    {
        if (_ensured) return;

        await _ensureOnce.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensured) return;

            var head = await _http
                .SendAsync(new HttpRequestMessage(HttpMethod.Head, _opt.DeadLetterIndex), ct)
                .ConfigureAwait(false);

            if (!head.IsSuccessStatusCode)
            {
                var body = new
                {
                    mappings = new
                    {
                        properties = new Dictionary<string, object>
                        {
                            ["eventId"] = new { type = "keyword" },
                            ["eventType"] = new { type = "keyword" },
                            ["queueName"] = new { type = "keyword" },
                            ["failedAt"] = new { type = "date" },
                            ["occurredAt"] = new { type = "date" },
                            ["attempts"] = new { type = "integer" },
                            ["replayCount"] = new { type = "integer" },
                            ["lastReplayAt"] = new { type = "date" }
                        }
                    }
                };

                var created = await _http
                    .PutAsJsonAsync(_opt.DeadLetterIndex, body, ct).ConfigureAwait(false);

                if (!created.IsSuccessStatusCode)
                {
                    var text = await created.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logger.LogWarning("创建死信索引失败：{Body}", text);
                }
            }

            _ensured = true;
        }
        finally
        {
            _ensureOnce.Release();
        }
    }
}
