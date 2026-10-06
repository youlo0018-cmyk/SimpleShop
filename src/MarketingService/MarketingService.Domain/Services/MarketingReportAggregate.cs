namespace MarketingService.Domain.Services;

/// <summary>券报表的聚合结果。</summary>
/// <param name="IssuedTotal">发放总数：活动库存里发出去多少张。</param>
/// <param name="ReceivedTotal">领取数：区间内客户实际领走的张数。</param>
/// <param name="ConsumedTotal">核销数：区间内真正用掉并产生优惠的张数。</param>
/// <param name="ConsumeRate">核销率 = 核销数 / 领取数，分母为 0 时为 0。</param>
/// <param name="DiscountTotal">折扣总额：核销掉的券实际让利了多少（两位小数）。</param>
/// <remarks>
/// <b>发放数与领取数不是一回事</b>：发放是运营配的库存，领取是客户真的拿了。
/// 运营常把「发了 1000 张」当成业绩，但只有被领走的才算触达。
/// 两个数分开给，后台才看得出「是发得不够」还是「发了没人用」。
/// </remarks>
public sealed record CouponReportAggregate(
    long IssuedTotal,
    long ReceivedTotal,
    long ConsumedTotal,
    decimal ConsumeRate,
    decimal DiscountTotal);

/// <summary>单个活动的参与聚合（活动报表的一行）。</summary>
/// <param name="ActivityId">活动 Id。</param>
/// <param name="ActivityName">活动名快照（活动改名后历史报表不跟着变）。</param>
/// <param name="OrderCount">参与订单数。</param>
/// <param name="DiscountTotal">折扣总额：这些订单因该活动实际让利合计。</param>
/// <param name="OrderNos">参与的订单号，交给调用方去订单服务取「参与金额」。</param>
/// <remarks>
/// <b>参与金额不在这里算</b>：它必须按「已支付、未取消、未退款」的口径算，
/// 那是订单服务的职责（与工作台 GMV 同源）。营销侧只提供「哪些单参与了」，
/// 两边各算一遍必然对不上账。
/// </remarks>
public sealed record ActivityParticipationAggregate(
    long ActivityId,
    string ActivityName,
    long OrderCount,
    decimal DiscountTotal,
    IReadOnlyList<string> OrderNos);
