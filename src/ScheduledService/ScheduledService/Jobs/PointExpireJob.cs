using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ScheduledService.Jobs;

/// <summary>积分过期扣减任务。</summary>
/// <remarks>
/// <para><b>为什么是每小时而不是每天 02:00</b>（BUSINESS.md 13.5 写的是 02:00）：
/// 定点跑的最大问题是<b>进程没起来就整天不跑</b>——发布、机器重启、依赖抖动都可能把那个点错过，
/// 而过期积分会一直挂在余额上，用户看得到、客服问不出。</para>
///
/// <para>而真正影响资金安全的部分<b>并不依赖这个任务</b>：积分消费在选批次时就按
/// <c>ExpireAt &gt; now</c> 过滤了（见 PointRepository），所以过期积分在任务跑之前就已经花不出去。
/// 这个任务负责的是「把过期积分从可用余额里扣掉并写流水」，即<b>账面正确</b>。</para>
///
/// <para>每小时跑一次既让账面最多滞后一小时，又天然能在进程重启后补上漏跑的批次，
/// 而且重复执行是幂等的（业务号 <c>EXP-{lotId}</c>）。</para>
/// </remarks>
public sealed class PointExpireJob : IJob
{
    private readonly HttpClient _http;
    private readonly ILogger<PointExpireJob> _logger;

    /// <summary>构造任务。</summary>
    /// <param name="http">指向积分服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public PointExpireJob(HttpClient http, ILogger<PointExpireJob> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "point_expire";

    /// <inheritdoc />
    public int IntervalSeconds => 3600;

    /// <inheritdoc />
    /// <remarks>
    /// 3 小时。积分服务那侧逐个批次处理，量大时会跑一阵；
    /// TTL 太短会导致锁先过期、两个实例同时进来——虽然处理本身幂等，
    /// 但重复扫几千个批次纯属浪费。
    /// </remarks>
    public int LockTtlSeconds => 10800;

    /// <inheritdoc />
    public async Task<JobRunResult> ExecuteAsync(CancellationToken ct)
    {
        var response = await _http
            .PostAsJsonAsync("internal/points/Expire", new ExpireRequest(500), ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return JobRunResult.Fail("过期扣减请求失败", $"HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content
            .ReadFromJsonAsync<ExpireResponse>(ct).ConfigureAwait(false);

        if (body is null)
        {
            return JobRunResult.Fail("过期扣减响应无法解析", "(空响应)");
        }

        if (!body.Success)
        {
            return JobRunResult.Fail("过期扣减业务失败", body.Message);
        }

        var data = body.Data;
        if (data is null || data.Scanned == 0)
        {
            return JobRunResult.Ok("没有到期积分");
        }

        if (data.Expired == 0 && data.Skipped > 0)
        {
            _logger.LogWarning("扫到 {Scanned} 个到期批次但一个都没过期掉（{Skipped} 个跳过），需人工看一眼", data.Scanned, data.Skipped);
        }

        return JobRunResult.Ok(
            $"扫描 {data.Scanned} 个批次，过期 {data.Expired} 个，跳过 {data.Skipped} 个，扣减 {data.DeductedTotal} 积分");
    }

    /// <summary>过期扣减请求体。</summary>
    /// <param name="Limit">单次最多处理多少个批次。</param>
    private sealed record ExpireRequest([property: JsonPropertyName("limit")] int Limit);

    /// <summary>统一响应体。</summary>
    /// <param name="Success">是否成功。</param>
    /// <param name="Message">提示消息。</param>
    /// <param name="Data">处理结果。</param>
    private sealed record ExpireResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("data")] ExpireData? Data);

    /// <summary>处理结果。</summary>
    /// <param name="Scanned">扫到的批次数。</param>
    /// <param name="Expired">过期批次数。</param>
    /// <param name="Skipped">跳过批次数。</param>
    /// <param name="DeductedTotal">扣减积分总数。</param>
    private sealed record ExpireData(
        [property: JsonPropertyName("scanned")] int Scanned,
        [property: JsonPropertyName("expired")] int Expired,
        [property: JsonPropertyName("skipped")] int Skipped,
        [property: JsonPropertyName("deductedTotal")] long DeductedTotal);
}