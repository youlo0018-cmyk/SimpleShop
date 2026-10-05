using MarketingService.Domain.Entities;
using MarketingService.Domain.Services;

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

    /// <summary>按区间聚合券效果，供营销效果报表使用。</summary>
    /// <param name="from">区间起（含）。</param>
    /// <param name="to">区间止（不含）。</param>
    /// <param name="merchantId">商户 Id，0 表示不限。</param>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>发放 / 领取 / 核销 / 核销率 / 折扣总额。</returns>
    /// <remarks>
    /// <b>领取按 receive_at、核销按 consume_at</b>，各自落在区间内才算数。
    /// 混用同一个时间基准会让「月初领、月底用」的券凭空消失。
    /// </remarks>
    Task<CouponReportAggregate> AggregateAsync(
        DateTime from, DateTime to, long merchantId, long platformId,
        CancellationToken ct = default);

    /// <summary>分页查券模板（后台）。</summary>
    /// <param name="page">页码，从 1 起。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="keyword">模板名关键字。</param>
    /// <param name="couponType">券类型过滤，0 表示不限。</param>
    /// <param name="status">状态过滤，0 表示不限。</param>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>模板列表与总数。幂等只读。</returns>
    Task<(List<CouponTemplate> Items, long Total)> PageTemplatesAsync(
        int page, int pageSize, string keyword, int couponType, int status, long platformId,
        CancellationToken ct = default);

    /// <summary>更新券模板的可变字段。</summary>
    /// <param name="template">携带 Id 与待更新字段的模板。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    /// <remarks>
    /// **不写 issued_quantity**：那是发放流程累加的计数，
    /// 让后台能直接改它就等于可以凭空造出「已发放」数据，报表随即失真。
    /// </remarks>
    Task<int> UpdateTemplateAsync(CouponTemplate template, CancellationToken ct = default);

    /// <summary>软删券模板。</summary>
    /// <param name="templateId">模板 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> DeleteTemplateAsync(long templateId, CancellationToken ct = default);

    /// <summary>分页查券活动（后台）。</summary>
    /// <param name="page">页码，从 1 起。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="keyword">活动名关键字。</param>
    /// <param name="status">状态过滤，0 表示不限。</param>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>活动列表与总数。幂等只读。</returns>
    Task<(List<CouponActivity> Items, long Total)> PageActivitiesAsync(
        int page, int pageSize, string keyword, int status, long platformId,
        CancellationToken ct = default);

    /// <summary>更新券活动的可变字段。</summary>
    /// <param name="activity">携带 Id 与待更新字段的活动。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    /// <remarks>
    /// **不写 claimed_quantity**：理由同 <see cref="UpdateTemplateAsync"/>。
    /// 另有一条更硬的约束：<c>ClaimQuantity</c> 不允许改到<b>小于已领取数</b>，
    /// 否则「发了 100 张、已领 80 张」会被改成「总量 50 张」，
    /// 报表上凭空多出 30 张不存在的券。
    /// </remarks>
    Task<int> UpdateActivityAsync(CouponActivity activity, CancellationToken ct = default);

    /// <summary>分页查用户券（后台「券核销记录」）。</summary>
    /// <param name="page">页码，从 1 起。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="status">券状态过滤，0 表示不限。</param>
    /// <param name="templateId">模板 Id，0 表示不限。</param>
    /// <param name="orderNo">订单号过滤，空表示不限。</param>
    /// <param name="keyword">券码关键字。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>用户券列表与总数。幂等只读。</returns>
    /// <remarks>
    /// <b>用户券表就是核销记录的数据源</b>：它一行 = 一张已发出的券，
    /// 带着领取时间、占用订单号与核销时间，不需要额外的 coupon_record 表。
    /// 另建一张记录表只会带来「两处数据对不上」的老问题。
    /// </remarks>
    Task<(List<UserCoupon> Items, long Total)> PageUserCouponsAsync(
        int page, int pageSize, int status, long templateId, string orderNo, string keyword,
        CancellationToken ct = default);

    /// <summary>按 Id 集合批量取券模板（给列表补 name 用）。</summary>
    /// <param name="templateIds">模板 Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的模板；<b>不保证与传入顺序一致</b>，调用方自行按 Id 建字典。</returns>
    /// <remarks>
    /// 存在的唯一理由是**避免 N+1**：券活动列表要显示模板名，
    /// 在循环里逐行 <c>GetTemplateAsync</c> 的话一页 20 行就是 20 次查询。
    /// 一次 IN 查询拿回整页。
    /// </remarks>
    Task<List<CouponTemplate>> ListTemplatesByIdsAsync(
        IReadOnlyCollection<long> templateIds, CancellationToken ct = default);
}
