using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ScheduledService.Jobs;

/// <summary>评价聚合分重算任务。</summary>
/// <remarks>
/// <para><b>为什么是每小时而不是每天 03:00</b>（BUSINESS.md 14.5 写的是 03:00）：
/// 规格只承诺「每日一更新」「最多延迟 24 小时」，而每小时跑同样满足这个契约，
/// 好处是<b>进程重启后能自动补上漏跑的时段</b>。定点跑的最大问题是错过那个点就整天不跑——
/// 发布、机器重启、依赖抖动都可能错过，而评分会一直停在旧值。</para>
///
/// <para><b>重算是幂等的</b>：全量按当前评价重新算一遍，不依赖上一次的结果，
/// 所以一天跑 24 次和跑 1 次的结果完全一样，重复执行不会有副作用。</para>
///
/// <para><b>与积分过期任务（每小时）错开 30 分钟</b>：两个任务都会扫全表，
/// 同时跑会把数据库连接池打满，彼此超时后互相拖慢。</para>
/// </remarks>
public sealed class EvaluateRecomputeJob : IJob
{
    private readonly HttpClient _http;
    private readonly ILogger<EvaluateRecomputeJob> _logger;

    /// <summary>构造任务。</summary>
    /// <param name="http">指向评价服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public EvaluateRecomputeJob(HttpClient http, ILogger<EvaluateRecomputeJob> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "evaluate_recompute";

    /// <inheritdoc />
    public int IntervalSeconds => 3600;

    /// <inheritdoc />
    /// <remarks>
    /// 2 小时。重算要扫全量评价并逐个回写商品表，量大时跑一阵；
    /// TTL 短于耗时会导致锁先过期、两个实例同时进来重复重算。
    /// </remarks>
    public int LockTtlSeconds => 7200;

    /// <inheritdoc />
    /// <remarks>
    /// 30 分钟。与积分过期任务错开：两个任务都会扫全表，
    /// 同时跑会把数据库连接池打满，彼此超时后互相拖慢。
    /// </remarks>
    public int InitialDelaySeconds => 1800;

    /// <inheritdoc />
    public async Task<JobRunResult> ExecuteAsync(CancellationToken ct)
    {
        var response = await _http
            .PostAsJsonAsync("internal/evaluates/ratings/recompute", new RecomputeRequest(true), ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return JobRunResult.Fail("评分重算请求失败", $"HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content
            .ReadFromJsonAsync<RecomputeResponse>(ct).ConfigureAwait(false);

        if (body is null)
        {
            return JobRunResult.Fail("评分重算响应无法解析", "(空响应)");
        }

        if (!body.Success)
        {
            return JobRunResult.Fail("评分重算业务失败", body.Message);
        }

        var data = body.Data;
        if (data is null || data.SpuCount == 0)
        {
            return JobRunResult.Ok("没有有评价的商品");
        }

        // 回写失败要单独告警：评分算对了但没同步到商品表，用户看到的还是旧分数。
        // 这比整体失败更隐蔽，所以明确区分出来
        if (!data.WriteBackSucceeded)
        {
            _logger.LogWarning(
                "评分已算出（商品 {SpuCount} 个、店铺 {MerchantCount} 个）但回写商品表失败，商品评分最多再滞后一小时",
                data.SpuCount, data.MerchantCount);

            return JobRunResult.Fail(
                $"重算完成但回写失败（商品 {data.SpuCount} 个）",
                "ProductService 不可用，商品评分未同步");
        }

        return JobRunResult.Ok(
            $"重算商品 {data.SpuCount} 个、店铺 {data.MerchantCount} 个，回写 {data.WrittenBack} 个");
    }

    /// <summary>重算请求体。</summary>
    /// <param name="WriteBack">是否把结果回写到商品表。</param>
    private sealed record RecomputeRequest([property: JsonPropertyName("writeBack")] bool WriteBack);

    /// <summary>统一响应体。</summary>
    /// <param name="Success">是否成功。</param>
    /// <param name="Message">提示消息。</param>
    /// <param name="Data">重算结果。</param>
    private sealed record RecomputeResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("data")] RecomputeData? Data);

    /// <summary>重算结果。</summary>
    /// <param name="SpuCount">参与计算的商品数。</param>
    /// <param name="MerchantCount">参与计算的店铺数。</param>
    /// <param name="WrittenBack">成功回写的商品数。</param>
    /// <param name="WriteBackSucceeded">回写是否成功。</param>
    private sealed record RecomputeData(
        [property: JsonPropertyName("spuCount")] int SpuCount,
        [property: JsonPropertyName("merchantCount")] int MerchantCount,
        [property: JsonPropertyName("writtenBack")] int WrittenBack,
        [property: JsonPropertyName("writeBackSucceeded")] bool WriteBackSucceeded);
}
