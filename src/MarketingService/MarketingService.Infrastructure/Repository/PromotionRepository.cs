using Collaboration.Domain.Context;
using Collaboration.Domain.Infrastructure;
using Collaboration.Domain.Repository;
using FreeSql;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;

namespace MarketingService.Infrastructure.Repository;

/// <summary>营销活动仓储实现。</summary>
/// <remarks>
/// <para><b>刻意不继承 <see cref="CrudRepository{T}"/></b>，两条理由：</para>
/// <list type="number">
/// <item>本仓储的读写都要<b>显式列出要改的字段</b>。通用基类用 <c>SetDto</c> 把实体所有属性
/// 写进 SET，包括 <c>platform_id</c> / <c>merchant_id</c> 这些不该被编辑接口改的列；
/// 而 <c>Db.Update&lt;T&gt;(entity)</c> 那条路在这个项目里又已经踩过坑（空 SET、返回 0、不报错）。</item>
/// <item>继承后再定义同名 <c>InsertAsync</c> / <c>UpdateAsync</c> 会触发 CS0108
/// 「隐藏继承的成员」——编译器提醒你：这里的行为和基类不一样，必须显式写 <c>new</c>。
/// 那就变成在向读代码的人暗示「它其实是个重写」，其实是两套实现。不如直接不继承。</item>
/// </list>
/// </remarks>
public sealed class PromotionRepository : IPromotionRepository
{
    private readonly IFreeSql _db;

    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public PromotionRepository(IFreeSql freeSql) => _db = freeSql;

    /// <inheritdoc />
    public async Task<List<PromotionActivity>> ListActiveAsync(
        long platformId, long sessionId, DateTime nowUtc, CancellationToken ct = default)
        => await _db.Select<PromotionActivity>()
            .Where(a => a.Status == 1
                        && a.StartTime <= nowUtc
                        && a.EndTime >= nowUtc
                        && (platformId <= 0 || a.PlatformId == platformId)
                        && (sessionId <= 0 || a.SessionId == sessionId))
            .OrderBy(a => a.SortOrder).OrderBy(a => a.Id)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<PromotionActivity?> GetAsync(long activityId, CancellationToken ct = default)
        => await _db.Select<PromotionActivity>().Where(a => a.Id == activityId).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<(List<PromotionActivity> Items, long Total)> ListAsync(
        int activityType, string keyword, int status, int page, int pageSize, CancellationToken ct = default)
    {
        var like = (keyword ?? string.Empty).Trim();

        var items = await _db.Select<PromotionActivity>()
            .Where(a => (activityType <= 0 || a.ActivityType == activityType)
                        && (status <= 0 || a.Status == status)
                        && (string.IsNullOrEmpty(like) || a.ActivityName.Contains(like)))
            .OrderByDescending(a => a.CreatedAt).OrderByDescending(a => a.Id)
            .Limit(pageSize).Offset((page - 1) * pageSize)
            .ToListAsync(ct);

        var total = await _db.Select<PromotionActivity>()
            .Where(a => (activityType <= 0 || a.ActivityType == activityType)
                        && (status <= 0 || a.Status == status)
                        && (string.IsNullOrEmpty(like) || a.ActivityName.Contains(like)))
            .CountAsync(ct);

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<long> InsertAsync(PromotionActivity activity, CancellationToken ct = default)
    {
        // 雪花 Id 与时间戳在这里显式填。实测 FreeSql 3.5 的 Aop.CurdBefore 在本项目调用链上
        // 没有触发，依赖它会让 Id 写成 0、CreatedAt 写成默认值 0001-01-01（CRUD 基类里也记了这个坑）。
        // 审计字段在这里显式填。实测 FreeSql 3.5 的 Aop.CurdBefore 在本项目调用链上没有触发，
        // 依赖它会让 Id 写成 0、CreatedAt 写成 0001-01-01（CRUD 基类里也记了这个坑）。
        var ctx = TenantContextHolder.Current;
        activity.Id = SnowflakeId.NewId();
        activity.CreatedAt = DateTime.UtcNow;
        activity.CreatedById = ctx.UserId;
        activity.CreatedByName = ctx.UserName;
        activity.OperationId = ctx.UserId;
        activity.OperationName = ctx.UserName;
        await _db.Insert(activity).ExecuteAffrowsAsync(ct);
        return activity.Id;
    }

    /// <inheritdoc />
    public async Task<int> UpdateAsync(PromotionActivity activity, CancellationToken ct = default)
    {
        // 显式 Where + Set：FreeSql 3.5 下 Db.Update<T>(entity) 在雪花主键上会生成空 SET，
        // 一条 SQL 都不发、返回 0 且不报错——接口回「成功」而数据纹丝不动。
        //
        // 操作人字段**只在这里**更新，创建人字段永远不进 SET——
        // 「这个活动是谁建的」一旦被编辑动作覆盖，就再也查不出配置错误是谁引进来的了。
        var ctx = TenantContextHolder.Current;
        return await _db.Update<PromotionActivity>()
            .Where(a => a.Id == activity.Id)
            .Set(a => new PromotionActivity
            {
                ActivityName = activity.ActivityName,
                ActivityType = activity.ActivityType,
                ThresholdAmount = activity.ThresholdAmount,
                DiscountAmount = activity.DiscountAmount,
                DiscountRate = activity.DiscountRate,
                GiftTemplateId = activity.GiftTemplateId,
                SessionId = activity.SessionId,
                TargetType = activity.TargetType,
                Targets = activity.Targets,
                StartTime = activity.StartTime,
                EndTime = activity.EndTime,
                PerOrderLimit = activity.PerOrderLimit,
                TotalQuantity = activity.TotalQuantity,
                SortOrder = activity.SortOrder,
                Status = activity.Status,
                OperationId = ctx.UserId,
                OperationName = ctx.UserName,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> SetStatusAsync(long activityId, int status, CancellationToken ct = default)
    {
        var ctx = TenantContextHolder.Current;
        return await _db.Update<PromotionActivity>()
            .Where(a => a.Id == activityId)
            .Set(a => new PromotionActivity
            {
                Status = status,
                OperationId = ctx.UserId,
                OperationName = ctx.UserName,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> GetPriorityAsync(long platformId, CancellationToken ct = default)
    {
        var row = await _db.Select<MarketingConfig>()
            .Where(a => a.PlatformId == platformId)
            .FirstAsync(ct);

        return row?.Priority ?? MarketingPriorities.CouponFirst;
    }

    /// <inheritdoc />
    public async Task<int> SoftDeleteAsync(long activityId, CancellationToken ct = default)
        => await _db.Update<PromotionActivity>()
            .Where(a => a.Id == activityId)
            .Set(a => new PromotionActivity
            {
                IsDeleted = true, DeletedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);
}