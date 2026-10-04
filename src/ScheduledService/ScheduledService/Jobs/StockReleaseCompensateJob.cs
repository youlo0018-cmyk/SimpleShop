using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ScheduledService.Jobs;

/// <summary>库存释放补偿重试任务。</summary>
/// <remarks>
/// <para>取消订单、超时关单、下单失败回滚都要「释放库存」，那是它们的**兜底动作**。
/// 兜底动作自己失败时，库存就<b>永久锁住</b>——商品一直卖不出去，
/// 而且没有任何痕迹说明发生过什么，只能靠人工翻日志。</para>
///
/// <para>所以释放失败要写 <c>pending_stock_release</c>（见 InventoryService 的
/// <c>ApplyStockHandler</c>），这个任务负责把它们重试回来。
/// 它是<b>重试</b>而不是「发现锁住就释放」——后者会误伤正常下单锁住的库存。</para>
/// </remarks>
public sealed class StockReleaseCompensateJob : IJob
{
    private readonly HttpClient _http;
    private readonly ILogger<StockReleaseCompensateJob> _logger;

    /// <summary>构造任务。</summary>
    /// <param name="http">指向库存服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public StockReleaseCompensateJob(HttpClient http, ILogger<StockReleaseCompensateJob> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "stock_release_compensate";

    /// <inheritdoc />
    public int IntervalSeconds => 300;

    /// <inheritdoc />
    public int LockTtlSeconds => 600;

    /// <inheritdoc />
    public int InitialDelaySeconds => 90;

    /// <inheritdoc />
    public async Task<JobRunResult> ExecuteAsync(CancellationToken ct)
    {
        var response = await _http
            .PostAsJsonAsync("internal/inventory/compensate-releases", new { limit = 200 }, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return JobRunResult.Fail("补偿重试请求失败", $"HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content
            .ReadFromJsonAsync<CompensateResponse>(ct)
            .ConfigureAwait(false);

        if (body is null || !body.Success)
        {
            return JobRunResult.Fail("补偿重试业务失败", body?.Message ?? "(空响应)");
        }

        var data = body.Data;
        if (data is null || data.Scanned == 0)
        {
            return JobRunResult.Ok("没有待补偿的库存释放");
        }

        if (data.GaveUp > 0)
        {
            _logger.LogError(
                "{GaveUp} 条库存释放补偿已达重试上限转人工，需要人工核对库存", data.GaveUp);
        }

        return JobRunResult.Ok(
            $"扫描 {data.Scanned} 条补偿，成功 {data.Succeeded}，仍失败 {data.Failed}，转人工 {data.GaveUp}");
    }

    /// <summary>库存服务返回的补偿结果。</summary>
    /// <param name="Success">是否成功。</param>
    /// <param name="Message">提示消息。</param>
    /// <param name="Data">补偿统计。</param>
    private sealed record CompensateResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("data")] CompensateData? Data);

    /// <summary>补偿统计。</summary>
    /// <param name="Scanned">扫描条数。</param>
    /// <param name="Succeeded">成功条数。</param>
    /// <param name="Failed">仍失败条数。</param>
    /// <param name="GaveUp">转人工条数。</param>
    private sealed record CompensateData(
        [property: JsonPropertyName("scanned")] int Scanned,
        [property: JsonPropertyName("succeeded")] int Succeeded,
        [property: JsonPropertyName("failed")] int Failed,
        [property: JsonPropertyName("gaveUp")] int GaveUp);
}
