using Collaboration.Domain.Repository;
using FreeSql;
using PermissionService.Domain.Entities;
using PermissionService.Domain.IRepository;

namespace PermissionService.Infrastructure.Repository;

/// <summary>
/// 权限点仓储实现。
/// </summary>
/// <remarks>
/// InsertAsync / UpdateAsync / GetByIdAsync / DeleteAsync 继承 CrudRepository——
/// 基类负责雪花 Id、审计时间戳与软删。不要重复实现，否则会绕过 Id 填充。
/// 软删过滤由 GlobalFilter 注入（DATA_SPEC 3.2.1）。
/// </remarks>
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
}