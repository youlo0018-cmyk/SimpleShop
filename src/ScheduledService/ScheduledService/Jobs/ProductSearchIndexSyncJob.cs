using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ScheduledService.Jobs;

/// <summary>商品搜索索引对账（补偿任务）。</summary>
/// <remarks>
/// <para>为什么需要它：上一轮落地搜索时，索引写失败是<b>只记日志不阻塞业务</b>的
/// ——这是对的，商品保存是主链路，ES 只是加速手段。
/// 代价就是索引会慢慢和库不一致，于是需要一个兜底把漂移补回来。</para>
///
/// <para>跑得**不频繁**（默认 10 分钟）：商品保存时同步写索引，正常情况下索引是准的，
/// 这个任务只在异常时才真的有活干。跑太勤只是白白扫一遍全表。</para>
///
/// <para>用差集对账而不是「删了重建」：重建期间索引是空的，那段时间用户搜索会得到零结果，
/// 而且越热门越容易触发重建。</para>
/// </remarks>
public sealed class ProductSearchIndexSyncJob : IJob
{
    private readonly HttpClient _http;
    private readonly ILogger<ProductSearchIndexSyncJob> _logger;

    /// <summary>构造任务。</summary>
    /// <param name="http">指向商品服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public ProductSearchIndexSyncJob(HttpClient http, ILogger<ProductSearchIndexSyncJob> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "product_search_index_sync";

    /// <inheritdoc />
    public int IntervalSeconds => 600;

    /// <inheritdoc />
    /// <remarks>15 分钟。扫一遍全表并逐个补写，量大时会跑一阵；
    /// TTL 短于单轮耗时会导致两个实例重叠扫描，白白双倍开销。</remarks>
    public int LockTtlSeconds => 900;

    /// <inheritdoc />
    public async Task<JobRunResult> ExecuteAsync(CancellationToken ct)
    {
        var response = await _http
            .PostAsJsonAsync("internal/products/search-index/sync",
                new SyncRequest(200, true), ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return JobRunResult.Fail("索引对账请求失败", $"HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content
            .ReadFromJsonAsync<SyncResponse>(ct).ConfigureAwait(false);

        if (body is null)
        {
            return JobRunResult.Fail("索引对账响应无法解析", "(空响应)");
        }

        if (!body.Success)
        {
            return JobRunResult.Fail("索引对账业务失败", body.Message);
        }

        var data = body.Data;
        if (data is null)
        {
            return JobRunResult.Fail("索引对账无返回数据", "(无 data)");
        }

        // 有漂移才值得记 Info：正常情况每轮都是「补 0 清 0」，天天记会淹掉真正的异常
        if (data.Missing > 0 || data.OrphansRemoved > 0 || data.Failed > 0)
        {
            _logger.LogWarning(
                "商品索引存在漂移并已修复：补写 {Missing}，清理孤儿 {Orphans}，仍失败 {Failed}。" +
                "若 Failed 持续不为 0，说明 ES 侧有问题，需要人工介入。",
                data.Missing, data.OrphansRemoved, data.Failed);
        }

        return JobRunResult.Ok(
            $"库 {data.DbProducts} 个，索引 {data.IndexedBefore} → {data.IndexedAfter}，" +
            $"补写 {data.Missing}，清理孤儿 {data.OrphansRemoved}，失败 {data.Failed}");
    }

    /// <summary>对账请求体。</summary>
    /// <param name="PageSize">每批处理量。</param>
    /// <param name="DeleteOrphans">是否清理孤儿文档。</param>
    private sealed record SyncRequest(
        [property: JsonPropertyName("pageSize")] int PageSize,
        [property: JsonPropertyName("deleteOrphans")] bool DeleteOrphans);

    /// <summary>统一响应体。</summary>
    /// <param name="Success">是否成功。</param>
    /// <param name="Message">提示消息。</param>
    /// <param name="Data">对账统计。</param>
    private sealed record SyncResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("data")] SyncData? Data);

    /// <summary>对账统计。</summary>
    /// <param name="DbProducts">库里的商品数。</param>
    /// <param name="IndexedBefore">对账前索引数。</param>
    /// <param name="IndexedAfter">对账后索引数。</param>
    /// <param name="Missing">补写数。</param>
    /// <param name="OrphansRemoved">清理孤儿数。</param>
    /// <param name="Failed">补写失败数。</param>
    private sealed record SyncData(
        [property: JsonPropertyName("dbProducts")] int DbProducts,
        [property: JsonPropertyName("indexedBefore")] int IndexedBefore,
        [property: JsonPropertyName("indexedAfter")] int IndexedAfter,
        [property: JsonPropertyName("missing")] int Missing,
        [property: JsonPropertyName("orphansRemoved")] int OrphansRemoved,
        [property: JsonPropertyName("failed")] int Failed);
}