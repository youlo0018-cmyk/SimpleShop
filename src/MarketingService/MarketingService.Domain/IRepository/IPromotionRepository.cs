using MarketingService.Domain.Entities;

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
}