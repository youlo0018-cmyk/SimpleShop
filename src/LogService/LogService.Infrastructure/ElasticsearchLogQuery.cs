using System.Net.Http.Json;
using System.Text.Json;
using LogService.Application;
using LogService.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogService.Infrastructure;

/// <summary>用原生 REST 查 Elasticsearch 的实现。</summary>
public sealed class ElasticsearchLogQuery : ILogQuery
{
    private readonly HttpClient _http;
    private readonly LogIndexOptions _opt;
    private readonly ILogger<ElasticsearchLogQuery> _logger;

    /// <summary>构造查询器。</summary>
    /// <param name="http">指向 ES 的 HttpClient。</param>
    /// <param name="options">索引配置。</param>
    /// <param name="logger">日志器。</param>
    public ElasticsearchLogQuery(
        HttpClient http,
        IOptions<LogIndexOptions> options,
        ILogger<ElasticsearchLogQuery> logger)
    {
        _http = http;
        _opt = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<LogPage> PagePvAsync(LogQueryCondition condition, CancellationToken ct = default)
        => SearchAsync(_opt.PvIndex, condition, LogKind.Pv, ct);

    /// <inheritdoc />
    public Task<LogPage> PageOperationAsync(LogQueryCondition condition, CancellationToken ct = default)
        => SearchAsync(_opt.OperationIndex, condition, LogKind.Operation, ct);

    /// <inheritdoc />
    public Task<LogPage> PageExceptionAsync(LogQueryCondition condition, CancellationToken ct = default)
        => SearchAsync(_opt.ExceptionIndex, condition, LogKind.Exception, ct);

    /// <inheritdoc />
    public async Task<string> GetExceptionStackAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.Empty;

        var response = await _http
            .GetAsync($"{_opt.ExceptionIndex}/_doc/{Uri.EscapeDataString(id)}", ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return string.Empty;

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
        return json.TryGetProperty("_source", out var source)
            && source.TryGetProperty("stackTrace", out var stack)
                ? stack.GetString() ?? string.Empty
                : string.Empty;
    }

    /// <summary>日志种类，决定取哪些字段。</summary>
    private enum LogKind
    {
        /// <summary>页面访问日志。</summary>
        Pv,

        /// <summary>写操作日志。</summary>
        Operation,

        /// <summary>异常日志。</summary>
        Exception
    }

    /// <summary>执行一次分页查询。</summary>
    /// <param name="index">索引名。</param>
    /// <param name="condition">查询条件。</param>
    /// <param name="kind">日志种类。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页结果。ES 不可用时返回空页而不是抛异常。</returns>
    private async Task<LogPage> SearchAsync(
        string index, LogQueryCondition condition, LogKind kind, CancellationToken ct)
    {
        var empty = new LogPage([], 0, condition.Page, condition.PageSize);
        if (string.IsNullOrWhiteSpace(_opt.Url)) return empty;

        var filters = BuildFilters(condition, kind);

        var body = new Dictionary<string, object?>
        {
            // from/size 分页在 10000 条以后会退化（ES 默认 max_result_window）。
            // 日志后台最多翻到几万条，超出后返回空页是可接受的——真要深翻应该走 search_after，
            // 而不是把 max_result_window 调到很大，那会让一次查询吃掉整个节点。
            ["from"] = (condition.Page - 1) * condition.PageSize,
            ["size"] = condition.PageSize,
            ["track_total_hits"] = true,
            ["query"] = new
            {
                // 属性名是 ES DSL 的字段名，所以要写成 @bool：
                // C# 里 bool 是关键字，不加 @ 编译不过。
                @bool = new
                {
                    filter = filters,
                    must = BuildMust(condition, kind)
                }
            },
            ["sort"] = new object[]
            {
                // 🔴 **不能**拿 _id 排序：ES 8 默认关闭 _id 的 fielddata，
                // 排序会直接 400（illegal_argument_exception）。
                // 这个坑只在真跑一次查询时才暴露——索引写得好好的，count 也有数，
                // 一搜就报错，很容易被误判成「数据没写进去」。
                // 想要稳定翻页应该用 search_after + PIT，而不是给 _id 开 fielddata
                //（那会让整个索引的内存占用暴涨）。
                new Dictionary<string, object> { ["occurredAt"] = new { order = "desc" } }
            }
        };

        HttpResponseMessage response;
        try
        {
            response = await _http
                .PostAsJsonAsync($"{index}/_search", body, JsonOpts, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "查询日志索引 {Index} 失败", index);
            return empty;
        }

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogError("查询日志索引 {Index} 失败：HTTP {Code} {Body}",
                index, (int)response.StatusCode, text);
            return empty;
        }

        var json = await response.Content
            .ReadFromJsonAsync<JsonElement>(JsonOpts, ct).ConfigureAwait(false);

        return MapPage(json, condition);
    }

    /// <summary>JSON 选项。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>构造 filter 条件（可缓存、可精确匹配的部分）。</summary>
    /// <param name="condition">查询条件。</param>
    /// <param name="kind">日志种类。</param>
    /// <returns>filter 数组。</returns>
    private static List<object> BuildFilters(LogQueryCondition condition, LogKind kind)
    {
        var filters = new List<object>();

        if (kind != LogKind.Exception)
        {
            var range = new Dictionary<string, object?>();
            if (condition.MinStatusCode > 0)
                range["gte"] = condition.MinStatusCode;
            if (range.Count > 0)
                filters.Add(new { range = new Dictionary<string, object> { ["statusCode"] = range } });
        }

        var terms = new List<object>();
        if (!string.IsNullOrWhiteSpace(condition.RequestId))
            terms.Add(new { term = new Dictionary<string, string> { ["requestId"] = condition.RequestId } });
        if (!string.IsNullOrWhiteSpace(condition.Path))
            terms.Add(new { term = new Dictionary<string, string> { ["path"] = condition.Path } });
        if (!string.IsNullOrWhiteSpace(condition.Method))
            terms.Add(new { term = new Dictionary<string, string> { ["method"] = condition.Method } });
        if (!string.IsNullOrWhiteSpace(condition.Service))
            terms.Add(new { term = new Dictionary<string, string> { ["service"] = condition.Service } });
        if (terms.Count > 0) filters.Add(new { @bool = new { must = terms } });

        if (condition.From.HasValue || condition.To.HasValue)
        {
            var range = new Dictionary<string, object?>();
            if (condition.From.HasValue) range["gte"] = condition.From.Value;
            if (condition.To.HasValue) range["lte"] = condition.To.Value;
            filters.Add(new { range = new Dictionary<string, object> { ["occurredAt"] = range } });
        }

        return filters;
    }

    /// <summary>构造 must 条件（模糊匹配的部分）。</summary>
    /// <param name="condition">查询条件。</param>
    /// <param name="kind">日志种类。</param>
    /// <returns>must 数组；无关键字时返回空数组。</returns>
    private static List<object> BuildMust(LogQueryCondition condition, LogKind kind)
    {
        var must = new List<object>();
        if (string.IsNullOrWhiteSpace(condition.Keyword)) return must;

        var keyword = condition.Keyword.Trim();

        // 只搜「这个索引真实存在的字段」。给不存在的字段配 should 不会报错，
        // 但会让它永远匹配不上，白白多算一轮却什么都不产出。
        var fields = kind switch
        {
            LogKind.Pv => new[] { "path", "clientIp", "userAgent" },
            LogKind.Operation => new[] { "path", "operatorName" },
            _ => new[] { "path", "message", "exceptionType" }
        };

        var should = fields
            .Select(f => (object)new
            {
                wildcard = new Dictionary<string, object>
                {
                    [f] = new { value = "*" + keyword + "*", case_insensitive = true }
                }
            })
            .ToList();

        // minimum_should_match 必须显式给 1：只有 should 没有 must 时，
        // ES 默认要求全部 should 命中，等于要求一个字段同时包含关键字——
        // 「点单」这种关键字会一条都搜不出来。
        must.Add(new { @bool = new { should, minimum_should_match = 1 } });
        return must;
    }

    /// <summary>把 ES 响应映射成分页结果。</summary>
    /// <param name="json">ES 响应。</param>
    /// <param name="condition">原查询条件，用于回填页码。</param>
    /// <returns>分页结果。</returns>
    private static LogPage MapPage(JsonElement json, LogQueryCondition condition)
    {
        var items = new List<LogView>();
        long total = 0;

        if (json.TryGetProperty("hits", out var hits))
        {
            if (hits.TryGetProperty("total", out var totalNode))
            {
                // ES 7+ 的 total 是对象 { value, relation }；老版本是数字。
                total = totalNode.ValueKind == JsonValueKind.Object
                    ? totalNode.TryGetProperty("value", out var v) ? v.GetInt64() : 0
                    : totalNode.GetInt64();
            }

            if (hits.TryGetProperty("hits", out var hitArray))
            {
                foreach (var hit in hitArray.EnumerateArray())
                {
                    if (!hit.TryGetProperty("_source", out var source)) continue;
                    items.Add(ReadView(hit, source));
                }
            }
        }

        return new LogPage(items, total, condition.Page, condition.PageSize);
    }

    /// <summary>读一条日志。</summary>
    /// <param name="hit">命中节点，含 _id。</param>
    /// <param name="source">_source 节点。</param>
    /// <returns>日志视图。</returns>
    private static LogView ReadView(JsonElement hit, JsonElement source)
    {
        var id = hit.TryGetProperty("_id", out var idNode) ? idNode.GetString() ?? string.Empty : string.Empty;

        return new LogView(
            id,
            ReadDate(source, "occurredAt"),
            ReadString(source, "service"),
            ReadString(source, "method"),
            ReadString(source, "path"),
            ReadInt(source, "statusCode"),
            ReadLong(source, "elapsedMs"),
            ReadString(source, "requestId"),
            ReadString(source, "operatorName"),
            ReadBool(source, "isAdmin"),
            ReadString(source, "message"));
    }

    /// <summary>读字符串字段。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>字符串值，缺失返回空串。</returns>
    private static string ReadString(JsonElement node, string name)
        => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>读整型字段。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>整型值，缺失返回 0。</returns>
    private static int ReadInt(JsonElement node, string name)
        => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : 0;

    /// <summary>读长整型字段。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>长整型值，缺失返回 0。</returns>
    private static long ReadLong(JsonElement node, string name)
        => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : 0L;

    /// <summary>读布尔字段。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>布尔值，缺失返回 false。</returns>
    private static bool ReadBool(JsonElement node, string name)
        => node.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.True;

    /// <summary>读时间字段。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>UTC 时间，缺失返回默认时间。</returns>
    /// <remarks>
    /// 必须显式 <c>ToUniversalTime</c>：ES 读回来的 DateTime 带 Kind=Local，
    /// 直接拿去跟 UTC 的查询条件比会差一个时区偏移，日志「少了几小时」。
    /// </remarks>
    private static DateTime ReadDate(JsonElement node, string name)
        => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && v.TryGetDateTime(out var dt)
                ? dt.ToUniversalTime()
                : default;
}
