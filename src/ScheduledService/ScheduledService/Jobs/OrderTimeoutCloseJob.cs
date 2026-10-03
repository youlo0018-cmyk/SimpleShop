using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ScheduledService.Jobs;

/// <summary>支付超时关单任务（BUSINESS.md 7.3：超时 30 分钟、每 30 秒扫一次）。</summary>
/// <remarks>
/// 不关单的直接后果：库存一直被锁着、积分一直被冻着、券一直被占着。
/// 用户不付款，那些资源就再也回不来——热门商品会被「幽灵订单」占死库存，
/// 最后变成「有货但所有人都买不了」。
/// </remarks>
public sealed class OrderTimeoutCloseJob : IJob
{
    private readonly HttpClient _http;
    private readonly ILogger<OrderTimeoutCloseJob> _logger;

    /// <summary>构造任务。</summary>
    /// <param name="http">指向订单服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public OrderTimeoutCloseJob(HttpClient http, ILogger<OrderTimeoutCloseJob> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "order_timeout_close";

    /// <inheritdoc />
    public int IntervalSeconds => 30;

    /// <inheritdoc />
    /// <remarks>
    /// 90 秒 = 间隔的 3 倍。一轮要逐单退库存 / 积分解冻 / 退券，
    /// 积压到几百张时会跑得比 30 秒久；锁 TTL 太短就会出现两轮重叠。
    /// </remarks>
    public int LockTtlSeconds => 90;

    /// <inheritdoc />
    public async Task<JobRunResult> ExecuteAsync(CancellationToken ct)
    {
        var response = await _http
            .PostAsJsonAsync("internal/orders/close-timeout", new CloseTimeoutRequest(string.Empty, 0), ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return JobRunResult.Fail("关单请求失败", $"HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content
            .ReadFromJsonAsync<CloseTimeoutResponse>(ct).ConfigureAwait(false);

        if (body is null)
        {
            return JobRunResult.Fail("关单响应无法解析", "(空响应)");
        }

        if (!body.Success)
        {
            // 业务失败也要当成失败记：订单服务说「配置缺失」之类的话，
            // 如果按成功处理，日志里全是「完成」，没人看得出其实一直没关单
            return JobRunResult.Fail("关单业务失败", body.Message);
        }

        var data = body.Data;
        if (data is null || data.Scanned == 0)
        {
            return JobRunResult.Ok("没有超时未支付的订单");
        }

        // 有关闭数量为 0 但扫到候选时，说明状态全被别人改过了，值得留一条 Info
        if (data.Closed == 0)
        {
            _logger.LogDebug("扫到 {Scanned} 张但一张都没关掉（状态都已被处理）", data.Scanned);
        }

        return JobRunResult.Ok($"扫描 {data.Scanned} 张，关单 {data.Closed} 张，跳过 {data.Skipped} 张");
    }

    /// <summary>关单请求体。</summary>
    /// <param name="OrderNo">留空表示扫全量。</param>
    /// <param name="Limit">留空用服务端配置的批量上限。</param>
    private sealed record CloseTimeoutRequest(
        [property: JsonPropertyName("orderNo")] string OrderNo,
        [property: JsonPropertyName("limit")] int Limit);

    /// <summary>统一响应体。</summary>
    /// <param name="Success">是否成功。</param>
    /// <param name="Message">提示消息。</param>
    /// <param name="Data">关单结果。</param>
    private sealed record CloseTimeoutResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("data")] CloseTimeoutResultData? Data);

    /// <summary>关单结果数据。</summary>
    /// <param name="Scanned">扫到的候选数。</param>
    /// <param name="Closed">关掉的张数。</param>
    /// <param name="Skipped">跳过的张数。</param>
    private sealed record CloseTimeoutResultData(
        [property: JsonPropertyName("scanned")] int Scanned,
        [property: JsonPropertyName("closed")] int Closed,
        [property: JsonPropertyName("skipped")] int Skipped);
}