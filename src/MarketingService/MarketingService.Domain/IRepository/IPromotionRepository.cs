using MarketingService.Domain.Entities;
using MarketingService.Domain.Services;

namespace MarketingService.Domain.IRepository;

/// <summary>营销活动仓储。</summary>
/// <remarks>
/// 读接口与写接口分开：读是结算页的高频路径（每屏一次），只取必要字段；
/// 写是后台低频操作，要能按 Id 取回完整配置。
/// </remarks>
public interface IPromotionRepository
{
    /// <summary>取当前时刻可参与计算的活动。</summary>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="sessionId">场次 Id，0 表示不限（普通活动都是 0）。</param>
    /// <param name="nowUtc">当前时间。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>状态启用且落在时间窗内的活动。</returns>
    /// <remarks>
    /// 时间窗在这里过滤而不是交给计算器：列表页一次试算要过几百个商品，
    /// 每次都把过期活动拉出来再逐个判时间，纯粹是白做的功。
    /// </remarks>
    Task<List<PromotionActivity>> ListActiveAsync(
        long platformId, long sessionId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>按 Id 取活动。</summary>
    /// <param name="activityId">活动 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>活动或 null。</returns>
    Task<PromotionActivity?> GetAsync(long activityId, CancellationToken ct = default);

    /// <summary>分页查活动（后台列表）。</summary>
    /// <param name="activityType">活动类型，0 表示全部。</param>
    /// <param name="keyword">按活动名模糊匹配。</param>
    /// <param name="status">状态，0 表示全部。</param>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当页活动与总条数。</returns>
    Task<(List<PromotionActivity> Items, long Total)> ListAsync(
        int activityType, string keyword, int status, int page, int pageSize, CancellationToken ct = default);

    /// <summary>插入活动。</summary>
    /// <param name="activity">活动。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新活动 Id。</returns>
    Task<long> InsertAsync(PromotionActivity activity, CancellationToken ct = default);

    /// <summary>更新活动。</summary>
    /// <param name="activity">活动，需带 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> UpdateAsync(PromotionActivity activity, CancellationToken ct = default);

    /// <summary>停用 / 启用活动。</summary>
    /// <param name="activityId">活动 Id。</param>
    /// <param name="status">目标状态。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> SetStatusAsync(long activityId, int status, CancellationToken ct = default);

    /// <summary>读某平台的优惠优先级。</summary>
    /// <param name="platformId">平台 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>优先级码，见 <see cref="MarketingPriorities"/>。</returns>
    /// <remarks>
    /// 没配过时返回<b>券优先</b>（默认值）而不是抛错：活动引擎刚上线、配置还没种进去时，
    /// 商品列表页应该照常显示（只是不打折），而不是整页 500。
    /// </remarks>
    Task<int> GetPriorityAsync(long platformId, CancellationToken ct = default);

    /// <summary>软删活动。</summary>
    /// <param name="activityId">活动 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> SoftDeleteAsync(long activityId, CancellationToken ct = default);

    /// <summary>记一条活动参与记录（下单试算时写，幂等）。</summary>
    /// <param name="record">参与记录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>本次新写入返回 true；同一单同一活动已记过返回 false。</returns>
    /// <remarks>
    /// 唯一索引 <c>(order_no, activity_id)</c> 兜底：下单试算会被重放
    /// （客户端重试、幂等键撞车），不判存在性的话「参与订单数」会被刷成两倍。
    /// </remarks>
    Task<bool> RecordParticipationAsync(MarketingActivityRecord record, CancellationToken ct = default);

    /// <summary>按活动聚合参与情况（活动报表）。</summary>
    /// <param name="from">区间起（含）。</param>
    /// <param name="to">区间止（不含）。</param>
    /// <param name="merchantId">商户 Id，0 表示不限。</param>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="limit">最多返回多少个活动（按参与订单数倒序）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>
    /// 逐活动的参与订单数、折扣总额与订单号（供调用方去订单服务取金额），
    /// 以及区间内的活动**总数** —— 总数大于返回条数时说明被 <paramref name="limit"/> 截断了。
    /// </returns>
    Task<ActivityParticipationResult> AggregateParticipationAsync(
        DateTime from, DateTime to, long merchantId, long platformId, int limit,
        CancellationToken ct = default);

    /// <summary>分页查参与记录（报表下钻订单明细）。</summary>
    /// <param name="activityId">活动 Id，0 表示不限。</param>
    /// <param name="from">区间起（含）。</param>
    /// <param name="to">区间止（不含）。</param>
    /// <param name="page">页码，从 1 起。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当页记录与总条数，按时间倒序。</returns>
    Task<(List<MarketingActivityRecord> Items, long Total)> PageParticipationAsync(
        long activityId, DateTime from, DateTime to, int page, int pageSize,
        CancellationToken ct = default);

    /// <summary>列出超过指定时长仍未被确认存在订单的参与记录候选。</summary>
    /// <param name="createdBefore">只取创建时间早于该时刻的记录（UTC）。</param>
    /// <param name="limit">单轮最多返回多少条。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>按创建时间升序排列的候选记录。</returns>
    /// <remarks>
    /// 下单试算会先写参与记录、再落订单；进程死在两步之间时记录会变成孤儿。
    /// 这里只负责「找候选」，是否真的不存在由订单服务的批量存在性接口确认，
    /// 绝不能因为「查不到」就删除——订单服务抖动时那会把真实订单的记录清掉。
    /// </remarks>
    Task<List<MarketingActivityRecord>> ListParticipationOrphansAsync(
        DateTime createdBefore, int limit, CancellationToken ct = default);

    /// <summary>按订单号软删活动参与记录（孤儿清理用）。</summary>
    /// <param name="orderNos">订单号集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    /// <remarks>软删而不是物理删：保留「曾经记过又清掉」的痕迹，便于排查对账问题。</remarks>
    Task<int> SoftDeleteParticipationByOrderNosAsync(
        IReadOnlyCollection<string> orderNos, CancellationToken ct = default);
}

/// <summary>参与聚合结果。</summary>
/// <param name="Items">逐活动聚合，已按「参与订单数倒序 → 活动 Id 倒序」排好，最多 limit 条。</param>
/// <param name="TotalActivities">
/// 区间内有参与记录的活动总数。**必须回给调用方**：报表只展示前 N 个活动，
/// 不告诉前端「被截断了」的话，运营看到的就是一份不完整却毫无提示的名单。
/// </param>
public sealed record ActivityParticipationResult(
    IReadOnlyList<ActivityParticipationAggregate> Items, int TotalActivities);
