namespace MarketingService.Domain.Services;

/// <summary>单场秒杀的效果聚合。</summary>
/// <param name="SessionId">场次 Id。</param>
/// <param name="SessionName">场次名。</param>
/// <param name="ParticipantCount">参与人数：点过「抢购」的不同客户数。</param>
/// <param name="GrabSuccessCount">抢购成功数：真的抢到并落下订单的次数。</param>
/// <param name="StockTotal">本场秒杀库存总量。</param>
/// <param name="StockSold">本场已抢出数量。</param>
/// <param name="SellOutRate">售罄率 = 已抢 / 总量。分母为 0 时为 0。</param>
/// <param name="OrderNos">成功抢购产生的订单号，用于向订单服务换 GMV。</param>
/// <remarks>
/// <b>「参与人数」与「抢购成功数」是两个数</b>：前者是点过的人，
/// 后者是抢到的人。把两者合成一个「参与数」的话，
/// 运营会以为「100 个人参与 = 100 次成功」，从而完全看不出秒杀是不是太难抢了。
/// </remarks>
public sealed record SeckillSessionAggregate(
    long SessionId,
    string SessionName,
    long ParticipantCount,
    long GrabSuccessCount,
    int StockTotal,
    int StockSold,
    decimal SellOutRate,
    IReadOnlyList<string> OrderNos);

/// <summary>秒杀效果报表汇总。</summary>
/// <param name="Sessions">逐场次明细，按结束时间倒序。</param>
/// <param name="TotalParticipants">全部场次的参与人数合计。</param>
/// <param name="TotalGrabSuccess">全部场次的抢购成功数合计。</param>
/// <param name="TotalGmv">全部场次的成交额合计（两位小数）。</param>
public sealed record SeckillReport(
    IReadOnlyList<SeckillSessionAggregate> Sessions,
    long TotalParticipants,
    long TotalGrabSuccess,
    decimal TotalGmv);
