using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ScheduledService.Jobs;

/// <summary>秒杀场次到点自动结束任务。</summary>
/// <remarks>
/// <para>秒杀发布时库存已经从常规池<b>划走</b>了（BUSINESS.md 12.4），
/// 靠的是「结束场次时把剩余的划回去」来闭环。于是「有没有人结束场次」
/// 直接决定了这批库存会不会丢。</para>
///
/// <para>运营通常会手动中止，但<b>正常打完的场次没人会去点结束</b>——
/// 它应该由时间自动触发。缺了这个任务的话，一个正常结束的场次会永远停在
/// 「进行中」，剩余库存永久锁在秒杀池里，<b>而且没有任何报错</b>：
/// 现象只是商品「一直缺货」，从外面完全看不出是这个原因。</para>
///
/// <para>60 秒一轮：秒杀的「结束」本身是分钟级的，再密没有意义；
/// 而间隔越大，到期后库存被锁住的时间越长。</para>
/// </remarks>
public sealed class SeckillSessionFinishJob : IJob
{
    private readonly HttpClient _http;
    private readonly ILogger<SeckillSessionFinishJob> _logger;

    /// <summary>构造任务。</summary>
    /// <param name="http">指向营销服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public SeckillSessionFinishJob(HttpClient http, ILogger<SeckillSessionFinishJob> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "seckill_session_finish";

    /// <inheritdoc />
    public int IntervalSeconds => 60;

    /// <inheritdoc />
    /// <remarks>120 秒：单轮最多 50 个场次，逐个回补，给得比锁 TTL 略小以免与下一轮重叠。</remarks>
    public int LockTtlSeconds => 120;

    /// <inheritdoc />
    public int InitialDelaySeconds => 30;

    /// <inheritdoc />
    public async Task<JobRunResult> ExecuteAsync(CancellationToken ct)
    {
        var response = await _http
            .PostAsJsonAsync(
                "internal/marketing/seckill/sessions/FinishExpired",
                new { limit = 50 },
                ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return JobRunResult.Fail("结束到期秒杀场次请求失败", $"HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content
            .ReadFromJsonAsync<FinishResponse>(ct)
            .ConfigureAwait(false);

        if (body is null || !body.Success)
        {
            return JobRunResult.Fail("结束到期秒杀场次业务失败", body?.Message ?? "(空响应)");
        }

        var data = body.Data;
        if (data is null || data.Scanned == 0)
        {
            return JobRunResult.Ok("没有到期的秒杀场次");
        }

        if (data.FailedItems.Count > 0)
        {
            // 🔴 回补失败必须报出来，不能只记一条「已处理」。
            // 这些商品的常规库存永远回不来了，货凭空消失。
            _logger.LogError(
                "{Count} 项秒杀库存回补失败，需人工核对：{Items}",
                data.FailedItems.Count, string.Join("、", data.FailedItems));
        }

        return JobRunResult.Ok(
            $"扫描 {data.Scanned} 个到期场次，结束 {data.Finished} 个，回补 {data.Released} 件库存"
            + (data.FailedItems.Count > 0 ? $"，{data.FailedItems.Count} 项失败" : string.Empty));
    }

    /// <summary>营销服务返回的响应。</summary>
    /// <param name="Success">是否成功。</param>
    /// <param name="Message">提示消息。</param>
    /// <param name="Data">统计。</param>
    private sealed record FinishResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("data")] FinishData? Data);

    /// <summary>结束统计。</summary>
    /// <param name="Scanned">扫描到的到期场次数。</param>
    /// <param name="Finished">真正结束的场次数。</param>
    /// <param name="Released">回补件数。</param>
    /// <param name="FailedItems">回补失败清单。</param>
    private sealed record FinishData(
        [property: JsonPropertyName("scanned")] int Scanned,
        [property: JsonPropertyName("finished")] int Finished,
        [property: JsonPropertyName("released")] int Released,
        [property: JsonPropertyName("failedItems")] List<string> FailedItems);
}
