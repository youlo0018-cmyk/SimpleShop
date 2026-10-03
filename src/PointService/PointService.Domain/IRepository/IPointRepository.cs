using PointService.Domain.Entities;

namespace PointService.Domain.IRepository;

/// <summary>积分操作结果。</summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="AlreadyApplied">是否命中幂等（重复请求，未真正变更）。</param>
/// <param name="Available">结果可用积分。</param>
/// <param name="Frozen">结果冻结积分。</param>
/// <param name="TotalEarned">累计发放。</param>
/// <param name="TotalUsed">累计消耗。</param>
/// <param name="Capped">本次因超出余额上限被截断（仅发放会有）。</param>
/// <param name="Error">失败原因。</param>
public sealed record PointOutcome(
    bool Succeeded,
    bool AlreadyApplied,
    long Available,
    long Frozen,
    long TotalEarned = 0,
    long TotalUsed = 0,
    bool Capped = false,
    string Error = "");

/// <summary>积分仓储。</summary>
/// <remarks>
/// 每个方法都自带三件事，缺一不可，所以它们必须是**仓储方法**而不是应用层拼几条 SQL：
/// <list type="bullet">
/// <item><b>幂等</b>：靠 point_record 的 (customer_id, biz_no, action) 唯一索引。</item>
/// <item><b>并发</b>：条件更新（WHERE 计数仍等于读到的值），把同一客户的并发操作串行化。</item>
/// <item><b>非负 / 上限</b>：计数越界就整体失败，不做部分应用。</item>
/// </list>
/// </remarks>
public interface IPointRepository
{
    /// <summary>发放积分。超出余额上限的部分截断不入账。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="source">来源，见 <see cref="PointSources"/>。</param>
    /// <param name="quantity">发放数量，必须为正。</param>
    /// <param name="bizNo">业务单号。</param>
    /// <param name="action">动作（earn / signin）。</param>
    /// <param name="remark">备注。</param>
    /// <param name="validDays">有效期天数，默认 365。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>发放结果。</returns>
    Task<PointOutcome> EarnAsync(
        long customerId, string source, long quantity, string bizNo, string action = PointActions.Earn,
        string remark = "", int validDays = PointRules.ValidDays, CancellationToken ct = default);

    /// <summary>下单冻结。FIFO 先到期先用。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="bizNo">业务单号（订单号），同一单号只冻一次。</param>
    /// <param name="quantity">冻结数量。</param>
    /// <param name="remark">备注。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>冻结结果。</returns>
    Task<PointOutcome> LockAsync(
        long customerId, string bizNo, long quantity, string remark = "", CancellationToken ct = default);

    /// <summary>取消 / 超时解冻：退回**原发放批次**，不重新计算有效期。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="bizNo">业务单号。</param>
    /// <param name="remark">备注。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>解冻结果。</returns>
    Task<PointOutcome> UnfreezeAsync(
        long customerId, string bizNo, string remark = "", CancellationToken ct = default);

    /// <summary>支付成功实扣：冻结积分就此消失，不退回可用。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="bizNo">业务单号。</param>
    /// <param name="remark">备注。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实扣结果。</returns>
    Task<PointOutcome> ConsumeAsync(
        long customerId, string bizNo, string remark = "", CancellationToken ct = default);

    /// <summary>退款按比例回收，<b>向上取整</b>，退回原发放批次。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="bizNo">业务单号。</param>
    /// <param name="refundRatio">退款比例 0~1。整单退款传 1。</param>
    /// <param name="remark">备注。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回收结果。</returns>
    Task<PointOutcome> RefundAsync(
        long customerId, string bizNo, decimal refundRatio, string remark = "", CancellationToken ct = default);

    /// <summary>每日签到发放。按服务端本地日期（Asia/Shanghai）判定当天，7 天一轮，断签清零。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="localDate">服务端本地日期。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>签到结果；Capped 表示当天已签过（重复点击）。</returns>
    /// <remarks>
    /// 这件事不能拆成「查 streak → 算积分 → 发放」三步：两个并发点击会算出两个连续天数。
    /// 所以 streak 的判断、连续天数的推进、积分发放必须在同一个事务里做完。
    /// </remarks>
    Task<(PointOutcome Outcome, int Streak, long Reward, bool AlreadySigned)> SignInAsync(
        long customerId, DateOnly localDate, CancellationToken ct = default);
    /// <summary>取积分账户，不存在返回 null。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>账户或 null。幂等只读。</returns>
    Task<PointAccount?> GetAccountAsync(long customerId, CancellationToken ct = default);

    /// <summary>分页查询积分流水。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="page">页码。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>流水列表与总数。幂等只读。</returns>
    Task<(List<PointRecord> Items, long Total)> QueryRecordsAsync(
        long customerId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>取某笔冻结批次的明细（各发放批次划走多少）。</summary>
    /// <param name="bizNo">业务单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>冻结明细。幂等只读。</returns>
    Task<List<PointLockLot>> GetLockLotsAsync(string bizNo, CancellationToken ct = default);

    /// <summary>取已到期但仍有剩余的批次（过期任务用）。</summary>
    /// <param name="nowUtc">当前 UTC 时间。</param>
    /// <param name="limit">最多取多少条。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>到期批次列表。幂等只读。</returns>
    Task<List<PointLot>> GetExpiredLotsAsync(DateTime nowUtc, int limit, CancellationToken ct = default);

    /// <summary>过期扣减：把过期批次剩余清零并写流水。</summary>
    /// <param name="lotId">批次 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>扣减结果。</returns>
    Task<PointOutcome> ExpireLotAsync(long lotId, CancellationToken ct = default);
}