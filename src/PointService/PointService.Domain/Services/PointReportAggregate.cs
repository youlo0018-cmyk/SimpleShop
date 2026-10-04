namespace PointService.Domain.Services;

/// <summary>积分报表的聚合结果。</summary>
/// <param name="EarnedTotal">发放总额：区间内**所有让可用积分增加**的动作合计。</param>
/// <param name="ConsumedTotal">消耗总额：实扣（钱已付出，积分就此消失）。</param>
/// <param name="ExpiredTotal">过期总额：到期作废。</param>
/// <param name="LockedTotal">冻结总额：下单占用中，<b>还没消失</b>，所以不计入消耗。</param>
/// <param name="CurrentBalance">当前总余额：全部账户的可用 + 冻结。</param>
/// <remarks>
/// <b>「冻结」不算消耗</b>是这张表最容易搞错的地方：`lock` 只是把可用挪到冻结，
/// 钱还没付（可能还会解冻回去）。把它算进「消耗」会让积分消耗额虚高，
/// 而实际上这笔积分还好好地挂着。
/// </remarks>
public sealed record PointReportAggregate(
    long EarnedTotal,
    long ConsumedTotal,
    long ExpiredTotal,
    long LockedTotal,
    long CurrentBalance);
