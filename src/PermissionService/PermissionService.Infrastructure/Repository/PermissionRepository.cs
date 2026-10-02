using Collaboration.Domain.Repository;
using FreeSql;
using PermissionService.Domain.Entities;
using PermissionService.Domain.IRepository;

namespace PermissionService.Infrastructure.Repository;

/// <summary>权限点仓储实现。软删与租户条件由 GlobalFilter 统一注册（DATA_SPEC 3.2.1）。</summary>
public sealed class PermissionRepository : CrudRepository<Permission>, IPermissionRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public PermissionRepository(IFreeSql freeSql) : base(freeSql)
    {
    }

    /// <inheritdoc />
    public async Task<List<Permission>> QueryAllAsync(bool onlyEnabled, CancellationToken ct = default)
    {
        var select = Db.Select<Permission>();
        if (onlyEnabled) select = select.Where(a => a.Status == 1);
        return await select.OrderBy(a => a.Level).OrderBy(a => a.SortOrder).OrderBy(a => a.Id).ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<List<Permission>> GetChildrenAsync(long parentId, CancellationToken ct = default)
        => await Db.Select<Permission>().Where(a => a.ParentId == parentId)
            .OrderBy(a => a.SortOrder).OrderBy(a => a.Id).ToListAsync(ct);

    /// <inheritdoc />
    public async Task<bool> ExistsByCodeAsync(string code, long excludeId = 0, CancellationToken ct = default)
        => await Db.Select<Permission>().Where(a => a.Code == code && a.Id != excludeId).AnyAsync(ct);

    /// <inheritdoc />
    public async Task<bool> ExistsByNameAsync(string name, long parentId, long excludeId = 0, CancellationToken ct = default)
        => await Db.Select<Permission>().Where(a => a.Name == name && a.ParentId == parentId && a.Id != excludeId).AnyAsync(ct);

    /// <inheritdoc />
    public async Task<List<Permission>> GetByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new List<Permission>();
        var list = ids.ToArray();
        return await Db.Select<Permission>().Where(a => list.Contains(a.Id)).ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<Permission?> GetByIdAsync(long id, CancellationToken ct = default)
        => await Db.Select<Permission>().Where(a => a.Id == id).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<long> InsertAsync(Permission permission, CancellationToken ct = default)
    {
        await Db.Insert(permission).ExecuteAffrowsAsync(ct);
        return permission.Id;
    }

    /// <inheritdoc />
    public async Task<int> UpdateAsync(Permission permission, CancellationToken ct = default)
        => await Db.Update<Permission>(permission).ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public async Task<int> DeleteAsync(long id, CancellationToken ct = default)
        => await Db.Update<Permission>().Where(a => a.Id == id)
            .Set(a => new Permission { IsDeleted = true, DeletedAt = DateTime.UtcNow })
            .ExecuteAffrowsAsync(ct);
}