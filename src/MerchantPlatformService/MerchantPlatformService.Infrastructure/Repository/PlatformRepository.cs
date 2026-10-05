using Collaboration.Domain.Infrastructure;
using Collaboration.Domain.Context;
using Collaboration.Domain.Repository;
using FreeSql;
using MerchantPlatformService.Domain.Entities;
using MerchantPlatformService.Domain.Exceptions;
using MerchantPlatformService.Domain.IRepository;

namespace MerchantPlatformService.Infrastructure.Repository;

/// <summary>平台仓储实现。</summary>
public sealed class PlatformRepository : CrudRepository<Platform>, IPlatformRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public PlatformRepository(IFreeSql freeSql) : base(freeSql) { }

    /// <summary>平台账号只能看/改自己那一条平台记录。</summary>
    /// <returns>超管返回 0（表示不限）；平台账号返回自己的平台 Id。</returns>
    /// <remarks>
    /// <b>为什么这条规则写在这里而不是全局租户过滤器里</b>：platform 是**租户根表**，
    /// 它的可见性规则与其它表相反 —— 其它表问「这一行归哪个平台所有」（对比 PlatformId 列），
    /// platform 表问「这一行是不是我」（对比 Id 列）。
    ///
    /// <para>把它塞进所有后台实体共用的租户条件里，会让 platform 行的
    /// <c>platform_id = 0</c> 被拿去和当前平台 Id 比较，平台账号连自己的资料都查不到。
    /// 因此过滤器注册器使用 <c>ApplyOnly&lt;TEntity&gt;</c>，只给非租户根实体挂租户条件；
    /// 平台根表的 <c>Id == self</c> 与软删条件在这里显式写清楚，SQL 可直接审阅。</para>
    /// </remarks>
    private static long SelfPlatformId()
    {
        var ctx = TenantContextHolder.Current;
        // 超管（PlatformId = 0）不受限，这是「能在系统里开新地盘」的那个角色
        return ctx.IsSuperAdmin ? 0 : ctx.PlatformId;
    }

    /// <inheritdoc />
    public new async Task<long> InsertAsync(Platform platform, CancellationToken ct = default)
    {
        try
        {
            return await base.InsertAsync(platform, ct);
        }
        catch (Exception ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            // 名称与编码各有唯一索引，这里要告诉前端到底是哪个撞了，
            // 否则只能回一句「已存在」，用户根本不知道该改哪个框
            var isCode = PostgresErrors.IsUniqueViolationOn(ex, "uk_platform_code");
            throw new PlatformCodeTakenException(isCode ? "platformCode" : "platformName");
        }
    }

    /// <inheritdoc />
    public new async Task<Platform?> GetByIdAsync(long platformId, CancellationToken ct = default)
    {
        var self = SelfPlatformId();
        return await Db.Select<Platform>()
            .Where(a => !a.IsDeleted)
            .Where(a => a.Id == platformId)
            // 平台账号查别人的平台要返回 null（→ 上层回 404），
            // 而不是「查到了但不让改」—— 后者会泄露「这个平台存在」。
            .Where(a => self <= 0 || a.Id == self)
            .FirstAsync(ct);
    }

    /// <inheritdoc />
    public async Task<Platform?> GetByCodeAsync(string platformCode, CancellationToken ct = default)
        => await Db.Select<Platform>()
            .Where(a => a.PlatformCode.ToUpper() == platformCode.ToUpper())
            .FirstAsync(ct);

    /// <inheritdoc />
    public new async Task<bool> UpdateAsync(Platform platform, CancellationToken ct = default)
        => await base.UpdateAsync(platform, ct) > 0;

    /// <inheritdoc />
    public async Task<bool> SoftDeleteAsync(long platformId, CancellationToken ct = default)
    {
        var self = SelfPlatformId();
        var affected = await Db.Update<Platform>()
            .Where(a => a.Id == platformId)
            // 平台账号删不掉别人的平台：条件带上 self，影响行数为 0 时上层回「平台不存在」
            .Where(a => self <= 0 || a.Id == self)
            .Set(a => new Platform
            {
                IsDeleted = true,
                DeletedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<PagedPlatforms> PageAsync(string? keyword, int status, int page, int pageSize,
        CancellationToken ct = default)
    {
        var self = SelfPlatformId();
        var query = Db.Select<Platform>()
            .Where(a => !a.IsDeleted)
            .Where(a => self <= 0 || a.Id == self);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var text = keyword.Trim();
            query = query.Where(a => a.PlatformName.Contains(text) || a.PlatformCode.Contains(text));
        }

        if (status > 0) query = query.Where(a => a.Status == status);

        var total = await query.CountAsync(ct);
        if (total == 0) return new PagedPlatforms([], 0, page, pageSize);

        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .OrderByDescending(a => a.Id)
            .Page(page, pageSize)
            .ToListAsync(ct);

        return new PagedPlatforms(items, total, page, pageSize);
    }

    /// <inheritdoc />
    public IReadOnlyList<Platform> ListEnabled(CancellationToken ct = default)
    {
        var self = SelfPlatformId();
        return Db.Select<Platform>()
            .Where(a => !a.IsDeleted)
            .Where(a => self <= 0 || a.Id == self)
            .Where(a => a.Status == PlatformStatuses.Enabled)
            .OrderBy(a => a.Id)
            .ToList();
    }
}
