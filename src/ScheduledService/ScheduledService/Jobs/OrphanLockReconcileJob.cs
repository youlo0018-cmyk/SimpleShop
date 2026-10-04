using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ScheduledService.Jobs;

/// <summary>孤儿预留对账：锁了很久却查无此单的库存，自动释放。</summary>
/// <remarks>
/// <para><b>为什么需要它</b>：下单链路是「锁库存 → 建订单」两步。进程恰好死在中间时，
/// 库存会<b>永远锁着</b>而订单根本不存在，没有别的路径能把它找回来。</para>
///
/// <para><b>三步，顺序不能换</b>：① 库存服务列出超期且未结算的锁定 →
/// ② 订单服务确认这些单号<b>确实不存在</b> → ③ 回去释放。
/// 把 ② 省掉就等于「锁超过 30 分钟就释放」，那会<b>误伤下单慢的用户</b>——
/// 下单到支付之间本来就可能超过 30 分钟。</para>
/// </remarks>
public sealed class OrphanLockReconcileJob : IJob
{
    /// <summary>锁定超过多少分钟才纳入候选。</summary>
    private const int OlderThanMinutes = 30;

    private readonly HttpClient _inventory;
    private readonly HttpClient _order;
    private readonly ILogger<OrphanLockReconcileJob> _logger;

    /// <summary>构造任务。</summary>
    /// <param name="inventory">指向库存服务的 HttpClient。</param>
    /// <param name="order">指向订单服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public OrphanLockReconcileJob(
        HttpClient inventory, HttpClient order, ILogger<OrphanLockReconcileJob> logger)
    {
        _inventory = inventory;
        _order = order;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "stock_orphan_reconcile";

    /// <inheritdoc />
    public int IntervalSeconds => 900;

    /// <inheritdoc />
    public int LockTtlSeconds => 1800;

    /// <inheritdoc />
    public int InitialDelaySeconds => 1500;

    /// <inheritdoc />
    public async Task<JobRunResult> ExecuteAsync(CancellationToken ct)
    {
        var resp = await _inventory
            .PostAsJsonAsync("internal/inventory/reconcile/orphan-locks",
                new { olderThanMinutes = OlderThanMinutes, limit = 200 }, ct)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            return JobRunResult.Fail("查询孤儿锁定候选失败", $"HTTP {(int)resp.StatusCode}");
        }

        var candidates = await resp.Content
            .ReadFromJsonAsync<ApiResponse<List<OrphanLock>>>(ct).ConfigureAwait(false);

        if (candidates is null || !candidates.Success
            || candidates.Data is null || candidates.Data.Count == 0)
        {
            return JobRunResult.Ok("没有疑似孤儿锁定");
        }

        // 业务单号是 {订单号}:{skuId}，取第一段就是订单号
        var orderNos = candidates.Data
            .Select(a => (a.BizNo ?? string.Empty).Split(':')[0])
            .Where(a => a.Length > 0)
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
            return JobRunResult.Fail("查询订单是否存在失败", $"HTTP {(int)existsResp.StatusCode}");
        }

        var exists = await existsResp.Content
            .ReadFromJsonAsync<ApiResponse<BatchExists>>(ct).ConfigureAwait(false);

        var missingSet = (exists?.Data?.Missing ?? []).ToHashSet(StringComparer.Ordinal);

        // 🔴 只释放「订单确实不存在」的。查不到订单 ≠ 订单不存在：
        // 订单服务挂了就一个都查不到，全放会**大面积超卖**。
        // 所以订单服务返回异常时这里直接失败退出，而不是当成「都不存在」。
        if (missingSet.Count == 0)
        {
            _logger.LogInformation("候选 {Count} 条全部仍有对应订单，不释放", candidates.Data.Count);
            return JobRunResult.Ok($"候选 {candidates.Data.Count} 条，均有对应订单");
        }

        var toRelease = candidates.Data
            .Where(a => missingSet.Contains((a.BizNo ?? string.Empty).Split(':')[0]))
            .Select(a => new { bizNo = a.BizNo, skuId = a.SkuId, quantity = a.Quantity })
            .ToList();

        var releaseResp = await _inventory
            .PostAsJsonAsync("internal/inventory/reconcile/release-orphans",
                new { locks = toRelease }, ct)
            .ConfigureAwait(false);

        if (!releaseResp.IsSuccessStatusCode)
        {
            return JobRunResult.Fail("释放孤儿锁定失败", $"HTTP {(int)releaseResp.StatusCode}");
        }

        var result = await releaseResp.Content
            .ReadFromJsonAsync<ApiResponse<ReleaseResult>>(ct).ConfigureAwait(false);

        var d = result?.Data;
        return JobRunResult.Ok(
            $"候选 {candidates.Data.Count} 条，确认无订单 {toRelease.Count} 条，" +
            $"释放 {d?.Released ?? 0} 条，跳过 {d?.Skipped ?? 0} 条，失败 {d?.Failed ?? 0} 条");
    }

    /// <summary>统一响应体。</summary>
    /// <param name="Success">是否成功。</param>
    /// <param name="Message">提示消息。</param>
    /// <param name="Data">业务数据。</param>
    private sealed record ApiResponse<T>(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("data")] T? Data);

    /// <summary>候选锁定。</summary>
    /// <param name="BizNo">业务单号。</param>
    /// <param name="SkuId">SKU Id。</param>
    /// <param name="Quantity">锁定数量。</param>
    private sealed record OrphanLock(
        [property: JsonPropertyName("bizNo")] string BizNo,
        [property: JsonPropertyName("skuId")] long SkuId,
        [property: JsonPropertyName("quantity")] int Quantity);

    /// <summary>订单存在性结果。</summary>
    /// <param name="Existing">存在的订单号。</param>
    /// <param name="Missing">不存在的订单号。</param>
    private sealed record BatchExists(
        [property: JsonPropertyName("existing")] IReadOnlyList<string>? Existing,
        [property: JsonPropertyName("missing")] IReadOnlyList<string>? Missing);

    /// <summary>释放结果。</summary>
    /// <param name="Released">成功释放条数。</param>
    /// <param name="Skipped">跳过条数。</param>
    /// <param name="Failed">失败条数。</param>
    private sealed record ReleaseResult(
        [property: JsonPropertyName("released")] int Released,
        [property: JsonPropertyName("skipped")] int Skipped,
        [property: JsonPropertyName("failed")] int Failed);
}
