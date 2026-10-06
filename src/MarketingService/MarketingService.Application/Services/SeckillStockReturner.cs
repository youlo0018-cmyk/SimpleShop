using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MarketingService.Application.Services;

/// <summary>把某个场次剩余的秒杀库存回补到常规池。</summary>
/// <remarks>
/// <para>抽成独立服务而不是各写一遍：<b>「手动中止」与「到点自动结束」是同一条库存动作</b>，
/// 两个入口必须走同一段代码。复制一份的后果是修一处漏一处——
/// 比如只给手动路径加了幂等单号，自动路径忘了，回补就可能被记成两次操作。</para>
///
/// <para><b>回补完成后才清标志</b>：中途挂掉时标志还在，重跑会继续回补。
/// 库存侧按 <c>bizNo</c> 幂等，所以重跑不会把货还两遍。</para>
/// </remarks>
public sealed class SeckillStockReturner
{
    private readonly ISeckillRepository _seckill;
    private readonly IInventoryPort _inventory;
    private readonly IDatabase _redis;
    private readonly ILogger<SeckillStockReturner> _logger;

    /// <summary>构造回补器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    /// <param name="inventory">库存端口。</param>
    /// <param name="redis">Redis 数据库，用于清秒杀余量键。</param>
    /// <param name="logger">日志器。</param>
    public SeckillStockReturner(
        ISeckillRepository seckill,
        IInventoryPort inventory,
        IDatabase redis,
        ILogger<SeckillStockReturner> logger)
    {
        _seckill = seckill;
        _inventory = inventory;
        _redis = redis;
        _logger = logger;
    }

    /// <summary>回补一个场次的剩余库存。</summary>
    /// <param name="session">场次。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回补件数与失败清单。</returns>
    public async Task<SeckillReturnOutcome> ReturnAsync(SeckillSession session, CancellationToken ct = default)
    {
        if (!session.StockTransferred)
        {
            return new SeckillReturnOutcome(0, Array.Empty<string>(), false);
        }

        var items = await _seckill.ListItemsAsync(session.Id, ct).ConfigureAwait(false);
        var failed = new List<string>();
        var released = 0;

        foreach (var item in items)
        {
            // 🔴 回补量取「库里的未售数」与「Redis 余量」的**较小者**。
            //
            // sold_count 是在**下单成功之后**才 +1 的，所以一个「预扣成功、还在下单」
            // 的请求此刻既没算进 sold_count、又已经占走了一件。只按
            // seckill_stock − sold_count 回补的话，这一件会被当成没卖出去而还给常规池，
            // 而那个请求随后照样把单落成 —— 结果是**回补多了一件、又卖出一件**，超卖。
            //
            // Redis 余量是「划出总数 − 已预扣」，天然把「在飞的那几件」排除在外，
            // 所以它才是真正可以还回去的数量。取较小者是双保险：
            // 万一 Redis 余量因为重试被抬高，也不会超过库里算出来的未售数。
            var dbRemaining = Math.Max(0, item.SeckillStock - item.SoldCount);
            var remaining = dbRemaining;

            var redisLeft = await _redis
                .StringGetAsync($"{SeckillStockKeys.Stock}{item.Id}").ConfigureAwait(false);
            if (redisLeft.HasValue
                && long.TryParse(redisLeft.ToString(), out var left)
                && left >= 0
                && left < remaining
                && left <= int.MaxValue)
            {
                remaining = (int)left;
            }

            if (remaining <= 0) continue;

            // 业务单号带 release 后缀：与划出时不同，
            // 所以库存侧不会把「回补」当成「又一次划出」而直接判成重复。
            var bizNo = $"SKL-RELEASE-{session.Id}-{item.SkuId}";
            try
            {
                var ok = await _inventory.ReleaseAsync(item.SkuId, remaining, bizNo, ct)
                    .ConfigureAwait(false);

                if (!ok)
                {
                    failed.Add($"{item.SkuId}（回补失败）");
                    continue;
                }

                released += remaining;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "场次 {SessionId} 回补 SKU {SkuId} 失败", session.Id, item.SkuId);
                failed.Add($"{item.SkuId}（库存服务异常）");
            }
        }

        // 清掉余量键：场次已经结束，这个数字不能再被谁读到。
        // 不清的话，下次重建同名场次时 KeyExists 会判定「已初始化」，
        // 余量就停留在上一场的旧值上——表现为「新场次一开始就说抢完了」。
        foreach (var item in items)
        {
            await _redis.KeyDeleteAsync($"{SeckillStockKeys.Stock}{item.Id}").ConfigureAwait(false);
        }

        await _seckill.SetStockTransferredAsync(session.Id, false, ct).ConfigureAwait(false);

        if (failed.Count > 0)
        {
            _logger.LogError(
                "场次 {SessionId} 有 {Count} 个商品回补失败，需人工核对：{Failed}",
                session.Id, failed.Count, string.Join("、", failed));
        }

        return new SeckillReturnOutcome(released, failed, true);
    }
}

/// <summary>回补结果。</summary>
/// <param name="Released">回补件数。</param>
/// <param name="Failed">失败清单（SKU 与原因）。</param>
/// <param name="HadStock">该场次是否划出过库存。没划过时其余两项都没有意义。</param>
public sealed record SeckillReturnOutcome(int Released, IReadOnlyList<string> Failed, bool HadStock);
