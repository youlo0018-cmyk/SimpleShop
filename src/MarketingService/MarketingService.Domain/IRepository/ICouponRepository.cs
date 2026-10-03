using MarketingService.Domain.Entities;

namespace MarketingService.Domain.IRepository;

/// <summary>券操作结果。</summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="AlreadyApplied">是否命中幂等（重复请求）。</param>
/// <param name="CouponId">涉及的用户券 Id。</param>
/// <param name="DiscountAmount">实际优惠金额。</param>
/// <param name="Error">失败原因。</param>
public sealed record CouponOutcome(
    bool Succeeded,
    bool AlreadyApplied,
    long CouponId,
    decimal DiscountAmount,
    string Error = "");

/// <summary>领券结果。</summary>
/// <param name="Outcome">操作结果。</param>
/// <param name="CouponCodes">本次发放的券码。</param>
public sealed record ClaimResult(CouponOutcome Outcome, IReadOnlyList<string> CouponCodes);

/// <summary>券仓储。</summary>
/// <remarks>
/// 写操作（领券 / 占券 / 核销 / 回退）都必须自带<b>幂等 + 并发控制</b>，
/// 所以它们是仓储方法而不是应用层拼 SQL：
/// <list type="bullet">
/// <item>领券要同时扣模板池子与活动池子，两个计数器都要条件更新。</item>
/// <item>占券要把「选中的券」从 1（未使用）改成 2（已占用），
/// 条件带上 status=1 才能保证同一张券不会被两个并发订单占用。</item>
/// </list>
/// </remarks>
public interface ICouponRepository
{
    /// <summary>从券活动领券。发放时把模板规则**快照**到用户券上。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="activityId">券活动 Id。</param>
    /// <param name="quantity">领取张数，1 表示单张。</param>
    /// <param name="nowUtc">当前 UTC 时间。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>领券结果。</returns>
    Task<ClaimResult> ClaimAsync(
        long customerId, long activityId, int quantity, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>占券（下单时锁定）。一笔订单最多占一张。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号。</param>
    /// <param name="couponId">要占的用户券 Id，0 表示由服务端自动选最优券。</param>
    /// <param name="lines">订单行，用于计算优惠。</param>
    /// <param name="nowUtc">当前 UTC 时间。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>占券结果，含实际优惠金额。</returns>
    Task<CouponOutcome> OccupyAsync(
        long customerId, string orderNo, long couponId,
        IReadOnlyList<Services.CouponOrderLine> lines, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>核销券（支付成功）。券作废，不退回券包。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>核销结果。</returns>
    Task<CouponOutcome> ConsumeAsync(long customerId, string orderNo, CancellationToken ct = default);

    /// <summary>回退占券（取消 / 超时关单）。券回到可用。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回退结果。</returns>
    Task<CouponOutcome> ReleaseAsync(long customerId, string orderNo, CancellationToken ct = default);

    /// <summary>列出客户当前可用的券（未使用、未过期）。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="nowUtc">当前 UTC 时间。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可用券列表。幂等只读。</returns>
    Task<List<UserCoupon>> ListAvailableAsync(long customerId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>按 Id 取券。</summary>
    /// <param name="couponId">券 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>券或 null。幂等只读。</returns>
    Task<UserCoupon?> GetCouponAsync(long couponId, CancellationToken ct = default);

    /// <summary>统计某客户已从某券活动领了多少张（限领校验用）。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="activityId">券活动 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已领张数。幂等只读。</returns>
    Task<int> CountClaimedAsync(long customerId, long activityId, CancellationToken ct = default);

    /// <summary>取券模板。</summary>
    /// <param name="templateId">模板 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>模板或 null。幂等只读。</returns>
    Task<CouponTemplate?> GetTemplateAsync(long templateId, CancellationToken ct = default);

    /// <summary>取券活动。</summary>
    /// <param name="activityId">活动 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>活动或 null。幂等只读。</returns>
    Task<CouponActivity?> GetActivityAsync(long activityId, CancellationToken ct = default);
}