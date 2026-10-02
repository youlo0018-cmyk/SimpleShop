using Collaboration.Domain.Repository;
using FreeSql;
using PermissionService.Domain.Entities;
using PermissionService.Domain.IRepository;

namespace PermissionService.Infrastructure.Repository;

/// <summary>角色仓储实现，含角色-权限与账号-角色绑定的读写。</summary>
public sealed class RoleRepository : CrudRepository<Role>, IRoleRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public RoleRepository(IFreeSql freeSql) : base(freeSql)
    {
    }

    /// <inheritdoc />
    public async Task<(List<Role> Items, long Total)> QueryPagedAsync(int page, int pageSize, string keyword, CancellationToken ct = default)
    {
        var select = Db.Select<Role>();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            select = select.Where(a => a.RoleName.Contains(kw) || a.Code.Contains(kw));
        }

        var total = await select.CountAsync(ct);
        // 追加第二排序键 Id，避免同排序值时翻页出现重复或遗漏
        var items = await select.OrderByDescending(a => a.Id).Page(page, pageSize).ToListAsync(ct);
        return (items, total);
    }

    /// <inheritdoc />
    public async Task<Role?> GetByIdAsync(long id, CancellationToken ct = default)
        => await Db.Select<Role>().Where(a => a.Id == id).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<Role?> GetByCodeAsync(string code, CancellationToken ct = default)
        => await Db.Select<Role>().Where(a => a.Code == code).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<bool> ExistsByNameAsync(string roleName, CancellationToken ct = default)
        => await Db.Select<Role>().Where(a => a.RoleName == roleName).AnyAsync(ct);

    /// <inheritdoc />
    public async Task<long> InsertAsync(Role role, CancellationToken ct = default)
    {
        await Db.Insert(role).ExecuteAffrowsAsync(ct);
        return role.Id;
    }

    /// <inheritdoc />
    public async Task<int> UpdateAsync(Role role, CancellationToken ct = default)
        => await Db.Update<Role>(role).ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public async Task<int> DeleteAsync(long id, CancellationToken ct = default)
        => await Db.Update<Role>().Where(a => a.Id == id)
            .Set(a => new Role { IsDeleted = true, DeletedAt = DateTime.UtcNow })
            .ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public async Task<List<long>> GetPermissionIdsAsync(long roleId, CancellationToken ct = default)
    {
        var rows = await Db.Select<RolePermission>().Where(a => a.RoleId == roleId).ToListAsync(ct);
        return rows.Select(x => x.PermissionId).ToList();
    }

    /// <inheritdoc />
    public Task<int> ReplaceRolePermissionsAsync(long roleId, IReadOnlyCollection<long> permissionIds, CancellationToken ct = default)
    {
        var ids = permissionIds.Distinct().ToArray();
        var affected = 0;

        // 先删后插必须原子：中间态会出现「角色一条权限都没有」，
        // 若此时有请求进来会被误判为无权限（DATA_SPEC 3.7）。
        Db.Transaction(() =>
        {
            affected += Db.Delete<RolePermission>().Where(a => a.RoleId == roleId).ExecuteAffrows();
            if (ids.Length == 0) return;

            var now = DateTime.UtcNow;
            affected += Db.Insert(new List<RolePermission>(ids.Select(pid => new RolePermission
            {
                RoleId = roleId,
                PermissionId = pid,
                CreatedAt = now
            }))).ExecuteAffrows();
        });

        return Task.FromResult(affected);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ResolvePermissionCodesAsync(long userId, CancellationToken ct = default)
    {
        // 分三步查而不是连表 join：FreeSql 3.5 的 Join 重载在链式调用时类型推断容易歧义，
        // 分步写更直白，也更容易看出每一步的过滤条件。
        var bindings = await Db.Select<UserRole>().Where(a => a.UserId == userId).ToListAsync(ct);
        if (bindings.Count == 0) return Array.Empty<string>();

        var roleIds = bindings.Select(x => x.RoleId).Distinct().ToArray();
        var rolePerms = await Db.Select<RolePermission>().Where(a => roleIds.Contains(a.RoleId)).ToListAsync(ct);
        if (rolePerms.Count == 0) return Array.Empty<string>();

        var permIds = rolePerms.Select(x => x.PermissionId).Distinct().ToArray();
        var perms = await Db.Select<Permission>().Where(a => permIds.Contains(a.Id)).ToListAsync(ct);

        // 只返回启用状态的权限点：停用后网关不再校验
        return perms.Where(a => a.Status == 1 && !string.IsNullOrEmpty(a.Code))
            .Select(a => a.Code).Distinct().ToList();
    }

    /// <inheritdoc />
    public async Task<List<Role>> GetUserRolesAsync(long userId, CancellationToken ct = default)
    {
        var roleIds = await Db.Select<UserRole>().Where(a => a.UserId == userId).ToListAsync(ct);
        if (roleIds.Count == 0) return new List<Role>();

        var ids = roleIds.Select(x => x.RoleId).ToArray();
        return await Db.Select<Role>().Where(a => ids.Contains(a.Id)).ToListAsync(ct);
    }

    /// <inheritdoc />
    public Task<int> ReplaceUserRolesAsync(long userId, IReadOnlyCollection<long> roleIds, long platformId, CancellationToken ct = default)
    {
        var ids = roleIds.Distinct().ToArray();
        var affected = 0;

        Db.Transaction(() =>
        {
            affected += Db.Delete<UserRole>().Where(a => a.UserId == userId).ExecuteAffrows();
            if (ids.Length == 0) return;

            var now = DateTime.UtcNow;
            affected += Db.Insert(new List<UserRole>(ids.Select(rid => new UserRole
            {
                UserId = userId,
                RoleId = rid,
                PlatformId = platformId,
                CreatedAt = now
            }))).ExecuteAffrows();
        });

        return Task.FromResult(affected);
    }

    /// <inheritdoc />
    public async Task<int> UnbindRoleAsync(long roleId, CancellationToken ct = default)
        => await Db.Delete<UserRole>().Where(a => a.RoleId == roleId).ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public async Task<bool> ExistsByCodeAsync(string code, CancellationToken ct = default)
        => await Db.Select<Role>().Where(a => a.Code == code).AnyAsync(ct);
}
