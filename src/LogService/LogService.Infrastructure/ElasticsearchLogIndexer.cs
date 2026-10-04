using System.Net.Http.Json;
using Collaboration.Domain.Messaging;
using LogService.Application;
using LogService.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogService.Infrastructure;

/// <summary>日志索引配置。</summary>
public sealed class LogIndexOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "LogIndex";

    /// <summary>Elasticsearch 地址。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>页面访问日志索引名。</summary>
    public string PvIndex { get; set; } = "simpleshop_log_pv";

    /// <summary>写操作日志索引名。</summary>
    public string OperationIndex { get; set; } = "simpleshop_log_operation";

    /// <summary>异常日志索引名。</summary>
    public string ExceptionIndex { get; set; } = "simpleshop_log_exception";

    /// <summary>死信索引名。</summary>
    public string DeadLetterIndex { get; set; } = "simpleshop_log_deadletter";
}

/// <summary>用原生 REST 写 Elasticsearch 的实现。</summary>
/// <remarks>
/// <b>刻意不引 Elastic 客户端包</b>：与 ProductService 的搜索索引保持同一套方式
/// （HttpClient + REST）。引客户端要多一个依赖，而日志写入只是「PUT 一个文档」，
/// 用不到客户端的聚合、批量、连接池那套能力。
/// </remarks>
public sealed class ElasticsearchLogIndexer : ILogIndexer
{
    private readonly HttpClient _http;
    private readonly LogIndexOptions _opt;
    private readonly ILogger<ElasticsearchLogIndexer> _logger;
    private readonly SemaphoreSlim _ensureOnce = new(1, 1);
    private bool _ensured;

    /// <summary>构造索引器。</summary>
    /// <param name="http">指向 ES 的 HttpClient。</param>
    /// <param name="options">索引配置。</param>
    /// <param name="logger">日志器。</param>
    public ElasticsearchLogIndexer(
        HttpClient http,
        IOptions<LogIndexOptions> options,
        ILogger<ElasticsearchLogIndexer> logger)
    {
        _http = http;
        _opt = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> IndexPvAsync(PvLogEntry entry, CancellationToken ct = default)
        => WriteAsync(_opt.PvIndex, entry, ct);

    /// <inheritdoc />
    public Task<bool> IndexOperationAsync(OperationLogEntry entry, CancellationToken ct = default)
        => WriteAsync(_opt.OperationIndex, entry, ct);

    /// <inheritdoc />
    public Task<bool> IndexExceptionAsync(ExceptionLogEntry entry, CancellationToken ct = default)
        => WriteAsync(_opt.ExceptionIndex, entry, ct);

    /// <summary>写一条文档，索引不存在时先建。</summary>
    /// <param name="index">索引名。</param>
    /// <param name="doc">文档内容。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    private async Task<bool> WriteAsync<T>(string index, T doc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.Url)) return false;

        await EnsureIndexAsync(index, ct);

        // 🔴 必须是 **POST** 而不是 PUT：
        // ES 的 `PUT /{index}/_doc` 是「按指定 Id 覆盖写」，不给 Id 会直接返回
        // 405 Incorrect HTTP method（allowed: POST）。想让 ES 自己生成文档 Id
        // 就得用 `POST /{index}/_doc`。日志不需要业务 Id，所以用 POST 最合适。
        var response = await _http
            .PostAsJsonAsync($"{index}/_doc", doc, EventJson.Options, ct)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode) return true;

        // 🔴 写失败必须**抛异常**：消费循环据此重试 / 丢死信。
        // 静默吞掉等于 ack 掉这条日志，数据永久丢失且没人知道
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        _logger.LogError("写日志索引 {Index} 失败：HTTP {Code} {Body}", index, (int)response.StatusCode, body);
        throw new InvalidOperationException($"写日志索引 {index} 失败：HTTP {(int)response.StatusCode}");
    }

    /// <summary>确保索引存在（幂等，只在首次与失败后重建时执行）。</summary>
    /// <param name="index">索引名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task EnsureIndexAsync(string index, CancellationToken ct)
    {
        if (_ensured) return;

        await _ensureOnce.WaitAsync(ct);
        try
        {
            if (_ensured) return;

            var head = await _http.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, index), ct).ConfigureAwait(false);

            if (!head.IsSuccessStatusCode)
            {
                var body = new
                {
                    mappings = new
                    {
                        properties = new Dictionary<string, object>
                        {
                            ["occurredAt"] = new { type = "date" },
                            ["requestId"] = new { type = "keyword" },
                            ["path"] = new { type = "keyword" },
                            ["method"] = new { type = "keyword" },
                            ["statusCode"] = new { type = "integer" },

                            // 🔴 service / operatorName / clientIp / userAgent **必须显式映射**。
                            // 不写的话 ES 动态映射成 text + .keyword 子字段，
                            // 而 `{"term":{"service":"LogService"}}` 打在 text 字段上
                            // 永远匹配不到——症状是「日志确实写进去了，但按服务名一条都查不出来」。
                            // 排查时很容易误判成「日志没写进去」，方向从一开始就错了。
                            ["service"] = new { type = "keyword" },
                            ["operatorName"] = new { type = "keyword" },
                            ["clientIp"] = new { type = "keyword" },
                            ["userAgent"] = new { type = "keyword" },

                            // message / stackTrace / exceptionType 保持动态映射（text）：
                            // 它们要全文模糊搜，映射成 keyword 就搜不动了。
                        }
                    }
                };

                var created = await _http
                    .PutAsJsonAsync(index, body, ct).ConfigureAwait(false);

                if (!created.IsSuccessStatusCode)
                {
                    var text = await created.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logger.LogWarning("创建日志索引 {Index} 失败：{Body}", index, text);
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
