using Collaboration.Domain.Infrastructure;
using Collaboration.Domain.Repository;
using FreeSql;
using MerchantPlatformService.Domain.Entities;
using MerchantPlatformService.Domain.Exceptions;
using MerchantPlatformService.Domain.IRepository;

namespace MerchantPlatformService.Infrastructure.Repository;

/// <summary>商户仓储实现。</summary>
public sealed class MerchantRepository : CrudRepository<Merchant>, IMerchantRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public MerchantRepository(IFreeSql freeSql) : base(freeSql) { }

    /// <inheritdoc />
    public new async Task<long> InsertAsync(Merchant merchant, CancellationToken ct = default)
    {
        try
        {
            return await base.InsertAsync(merchant, ct);
        }
        catch (Exception ex) when (PostgresErrors.IsUniqueViolationOn(ex, "uk_merchant_platform_name"))
        {
            throw new MerchantNameTakenException();
        }
    }

    /// <inheritdoc />
    public new async Task<Merchant?> GetByIdAsync(long merchantId, CancellationToken ct = default)
        => await Db.Select<Merchant>().Where(a => a.Id == merchantId).FirstAsync(ct);

    /// <inheritdoc />
    public new async Task<bool> UpdateAsync(Merchant merchant, CancellationToken ct = default)
        => await base.UpdateAsync(merchant, ct) > 0;

    /// <inheritdoc />
    public async Task<bool> SoftDeleteAsync(long merchantId, CancellationToken ct = default)
    {
        var affected = await Db.Update<Merchant>()
            .Where(a => a.Id == merchantId)
            .Set(a => new Merchant
            {
                IsDeleted = true,
                DeletedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<long> CountByPlatformAsync(long platformId, CancellationToken ct = default)
        => await Db.Select<Merchant>()
            .Where(a => a.PlatformId == platformId)
            .CountAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, long>> CountGroupByPlatformAsync(
        IReadOnlyCollection<long> platformIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, long>();
        if (platformIds.Count == 0) return result;

        var rows = await Db.Select<Merchant>()
            .Where(a => platformIds.Contains(a.PlatformId))
            .ToListAsync(a => a.PlatformId, ct);

        foreach (var group in rows.GroupBy(a => a))
        {
            result[group.Key] = group.Count();
        }

        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<Merchant> ListEnabledByPlatform(long platformId, CancellationToken ct = default)
        => Db.Select<Merchant>()
            .Where(a => a.PlatformId == platformId && a.Status == PlatformStatuses.Enabled)
            .OrderBy(a => a.Id)
            .ToList();

    /// <inheritdoc />
    public async Task<PagedMerchants> PageAsync(MerchantFilter filter, CancellationToken ct = default)
    {
        var query = Db.Select<Merchant>();

        if (filter.PlatformId > 0) query = query.Where(a => a.PlatformId == filter.PlatformId);
        if (filter.AuditStatus > 0) query = query.Where(a => a.AuditStatus == filter.AuditStatus);
        if (filter.Status > 0) query = query.Where(a => a.Status == filter.Status);

        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            var text = filter.Keyword.Trim();
            query = query.Where(a => a.MerchantName.Contains(text) || a.MerchantNo.Contains(text));
        }

        var total = await query.CountAsync(ct);
        if (total == 0) return new PagedMerchants([], 0, filter.Page, filter.PageSize);

        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .OrderByDescending(a => a.Id)
            .Page(filter.Page, filter.PageSize)
            .ToListAsync(ct);

        return new PagedMerchants(items, total, filter.Page, filter.PageSize);
    }

    /// <inheritdoc />
    public async Task<int> TryUpdateAuditAsync(long merchantId, int expectedAuditStatus,
        int newAuditStatus, string auditRemark, long auditorId, string auditorName,
        CancellationToken ct = default, int? newStatus = null)
    {
        var update = Db.Update<Merchant>()
            // 带上「当前审核状态」作为条件：两个管理员同时点审核时，
            // 只有一个能把状态从 10 改成 20，另一个拿到 0 就该直接返回，
            // 而不是把审核人 / 审核时间覆盖掉
            .Where(a => a.Id == merchantId && a.AuditStatus == expectedAuditStatus)
            .Set(a => new Merchant
            {
                AuditStatus = newAuditStatus,
                AuditRemark = auditRemark,
                AuditedAt = DateTime.UtcNow,
                AuditorId = auditorId,
                AuditorName = auditorName,
                UpdatedAt = DateTime.UtcNow
            });

        // 审核通过时顺带启用商户。Set 两次会生成两条 UPDATE 语句，
        // 所以这里是往**同一个** Set 里追加，而不是再链一次 —— 必须是同一条语句，
        // 否则中间崩掉就留下「已通过审核但仍停用」的商户（详见接口注释）。
        if (newStatus.HasValue)
        {
            var status = newStatus.Value;
            update = update.Set(a => new Merchant { Status = status, UpdatedAt = DateTime.UtcNow });
        }

        return await update.ExecuteAffrowsAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> TryUpdateStatusAsync(long merchantId, int expectedStatus, int newStatus,
        CancellationToken ct = default)
        => await Db.Update<Merchant>()
            // 带上「当前启停状态」作为条件：两个管理员同时点「停用」时只有一个生效，
            // 另一个拿到 0 后回「状态已变化」，而不是覆盖回去
            .Where(a => a.Id == merchantId && a.Status == expectedStatus)
            .Set(a => new Merchant
            {
                Status = newStatus,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public async Task<int> SyncRatingsAsync(IReadOnlyDictionary<long, decimal> ratings,
        CancellationToken ct = default)
    {
        if (ratings.Count == 0) return 0;

        var updated = 0;
        foreach (var (merchantId, rating) in ratings)
        {
            // 条件更新带上「当前值」，避免与后台的编辑操作互相覆盖
            var affected = await Db.Update<Merchant>()
                .Where(a => a.Id == merchantId)
                .Set(a => new Merchant { Rating = rating, UpdatedAt = DateTime.UtcNow })
                .ExecuteAffrowsAsync(ct);

            updated += affected;
        }

        return updated;
    }
}
