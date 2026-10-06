using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ScheduledService.Jobs;

/// <summary>活动参与记录孤儿清理：下单试算写了记录、订单却没落成的，按订单号对账删除。</summary>
/// <remarks>
/// <para><b>为什么需要它</b>：下单试算先写 <c>marketing_activity_record</c>，之后才落订单；
/// 进程死在两步之间、或落单前某一步失败，参与记录就会永久留在库里。
/// 报表按记录数统计「参与订单数」，这条记录会让运营看到一个根本不存在的订单。</para>
///
/// <para><b>三步，顺序不能换</b>：① 营销服务列出创建超过 30 分钟的候选 →
/// ② 订单服务确认这些订单号<b>确实不存在</b> → ③ 回营销服务软删。
/// 把 ② 省掉就等于「超过 30 分钟就删」，订单服务抖动时会清掉真实订单的记录。</para>
/// </remarks>
public sealed class ActivityRecordCleanupJob : IJob
{
    /// <summary>只处理创建超过多少分钟的候选，给正常下单留足落库时间。</summary>
    private const int OlderThanMinutes = 30;

    /// <summary>单轮最多处理多少条候选。</summary>
    private const int Limit = 200;

    private readonly HttpClient _marketing;
    private readonly HttpClient _order;
    private readonly ILogger<ActivityRecordCleanupJob> _logger;

    /// <summary>构造任务。</summary>
    /// <param name="marketing">指向营销服务的 HttpClient。</param>
    /// <param name="order">指向订单服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public ActivityRecordCleanupJob(
        HttpClient marketing, HttpClient order, ILogger<ActivityRecordCleanupJob> logger)
    {
        _marketing = marketing;
        _order = order;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "activity_record_orphan_cleanup";

    /// <inheritdoc />
    public int IntervalSeconds => 900;

    /// <inheritdoc />
    public int LockTtlSeconds => 1800;

    /// <inheritdoc />
    public int InitialDelaySeconds => 1800;

    /// <inheritdoc />
    public async Task<JobRunResult> ExecuteAsync(CancellationToken ct)
    {
        var candidatesResp = await _marketing
            .PostAsJsonAsync(
                "internal/marketing/activities/orphan-candidates",
                new { olderThanMinutes = OlderThanMinutes, limit = Limit },
                ct)
            .ConfigureAwait(false);

        if (!candidatesResp.IsSuccessStatusCode)
        {
            return JobRunResult.Fail(
                "查询活动参与记录孤儿候选失败", $"HTTP {(int)candidatesResp.StatusCode}");
        }

        var candidates = await candidatesResp.Content
            .ReadFromJsonAsync<ApiResponse<List<OrphanCandidate>>>(ct)
            .ConfigureAwait(false);

        if (candidates is null || !candidates.Success || candidates.Data is null)
        {
            return JobRunResult.Fail("查询活动参与记录孤儿候选失败", candidates?.Message ?? "(空响应)");
        }

        if (candidates.Data.Count == 0)
        {
            return JobRunResult.Ok("没有活动参与记录孤儿候选");
        }

        var orderNos = candidates.Data
            .Select(a => a.OrderNo)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (orderNos.Count == 0)
        {
            return JobRunResult.Ok($"候选 {candidates.Data.Count} 条，但都解析不出订单号");
        }

        var existsResp = await _order
            .PostAsJsonAsync("internal/orders/batch-exists", new { orderNos }, ct)
            .ConfigureAwait(false);

        if (!existsResp.IsSuccessStatusCode)
        {
            return JobRunResult.Fail(
                "查询订单是否存在失败", $"HTTP {(int)existsResp.StatusCode}");
        }

        var exists = await existsResp.Content
            .ReadFromJsonAsync<ApiResponse<BatchExists>>(ct)
            .ConfigureAwait(false);

        if (exists is null || !exists.Success || exists.Data is null)
        {
            return JobRunResult.Fail("查询订单是否存在失败", exists?.Message ?? "(空响应)");
        }

        var missing = (exists.Data.Missing ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (missing.Count == 0)
        {
            _logger.LogInformation(
                "活动参与记录候选 {Count} 条，全部仍有对应订单，不清理", candidates.Data.Count);
            return JobRunResult.Ok($"候选 {candidates.Data.Count} 条，均有对应订单");
        }

        var discardResp = await _marketing
            .PostAsJsonAsync(
                "internal/marketing/activities/discard-orphans",
                new { orderNos = missing },
                ct)
            .ConfigureAwait(false);

        if (!discardResp.IsSuccessStatusCode)
        {
            return JobRunResult.Fail(
                "清理活动参与记录孤儿失败", $"HTTP {(int)discardResp.StatusCode}");
        }

        var discarded = await discardResp.Content
            .ReadFromJsonAsync<ApiResponse<int>>(ct)
            .ConfigureAwait(false);

        if (discarded is null || !discarded.Success)
        {
            return JobRunResult.Fail("清理活动参与记录孤儿失败", discarded?.Message ?? "(空响应)");
        }

        return JobRunResult.Ok(
            $"候选 {candidates.Data.Count} 条，确认无订单 {missing.Count} 个，清理 {discarded.Data} 条");
    }

    /// <summary>统一响应体。</summary>
    /// <param name="Success">是否成功。</param>
    /// <param name="Message">提示消息。</param>
    /// <param name="Data">业务数据。</param>
    private sealed record ApiResponse<T>(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("data")] T? Data);

    /// <summary>孤儿参与记录候选。</summary>
    /// <param name="OrderNo">订单号。</param>
    /// <param name="ActivityId">活动 Id。</param>
    /// <param name="ActivityName">活动名快照。</param>
    private sealed record OrphanCandidate(
        [property: JsonPropertyName("orderNo")] string OrderNo,
        [property: JsonPropertyName("activityId")] long ActivityId,
        [property: JsonPropertyName("activityName")] string ActivityName);

    /// <summary>订单存在性结果。</summary>
    /// <param name="Existing">存在的订单号。</param>
    /// <param name="Missing">不存在的订单号。</param>
    private sealed record BatchExists(
        [property: JsonPropertyName("existing")] IReadOnlyList<string>? Existing,
        [property: JsonPropertyName("missing")] IReadOnlyList<string>? Missing);
}
