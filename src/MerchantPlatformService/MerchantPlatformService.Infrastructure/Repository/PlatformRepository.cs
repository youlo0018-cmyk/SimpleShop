using Collaboration.Domain.Infrastructure;
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
        => await Db.Select<Platform>().Where(a => a.Id == platformId).FirstAsync(ct);

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
        var affected = await Db.Update<Platform>()
            .Where(a => a.Id == platformId)
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
        var query = Db.Select<Platform>();

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
        => Db.Select<Platform>()
            .Where(a => a.Status == PlatformStatuses.Enabled)
            .OrderBy(a => a.Id)
            .ToList();
}
