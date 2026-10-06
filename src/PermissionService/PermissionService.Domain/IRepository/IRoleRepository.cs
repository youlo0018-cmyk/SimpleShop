using PermissionService.Domain.Entities;

namespace PermissionService.Domain.IRepository;

/// <summary>角色仓储，含角色-权限点与账号-角色的绑定读写。</summary>
public interface IRoleRepository
{
    /// <summary>分页查询角色。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数，1-100。</param>
    /// <param name="keyword">按角色名或编码模糊搜索。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>角色列表与总数。幂等只读。</returns>
    Task<(List<Role> Items, long Total)> QueryPagedAsync(int page, int pageSize, string keyword, CancellationToken ct = default);

    /// <summary>按 Id 取角色，不存在返回 null。</summary>
    /// <param name="id">角色 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>角色或 null。幂等只读。</returns>
    Task<Role?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>按 Id 集合取角色。</summary>
    /// <param name="ids">角色 Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的角色列表，顺序不保证。幂等只读。</returns>
    Task<List<Role>> GetByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// <summary>按角色编码取角色，不存在返回 null。</summary>
    /// <param name="code">角色编码。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>角色或 null。幂等只读。</returns>
    Task<Role?> GetByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>插入角色。</summary>
    /// <param name="role">角色实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新角色 Id。非幂等。</returns>
    Task<long> InsertAsync(Role role, CancellationToken ct = default);

    /// <summary>按字段更新角色。</summary>
    /// <param name="role">携带 Id 与待更新字段的实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> UpdateAsync(Role role, CancellationToken ct = default);

    /// <summary>取角色已绑定的权限点 Id 集合。</summary>
    /// <param name="roleId">角色 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>权限点 Id 列表。幂等只读。</returns>
    Task<List<long>> GetPermissionIdsAsync(long roleId, CancellationToken ct = default);

    /// <summary>重绑角色权限：先删后插，原子执行。</summary>
    /// <param name="roleId">角色 Id。</param>
    /// <param name="permissionIds">目标权限点 Id 集合，可为空表示清空。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等：重复绑定同一集合结果一致。</returns>
    Task<int> ReplaceRolePermissionsAsync(long roleId, IReadOnlyCollection<long> permissionIds, CancellationToken ct = default);

    /// <summary>解析某账号的全部权限点编码。</summary>
    /// <param name="userId">后台账号 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>权限编码集合。无绑定返回空集合（fail-closed，不做任何兜底）。幂等只读。</returns>
    Task<IReadOnlyList<string>> ResolvePermissionCodesAsync(long userId, CancellationToken ct = default);

    /// <summary>解析某账号绑定的角色。</summary>
    /// <param name="userId">后台账号 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>角色列表。幂等只读。</returns>
    Task<List<Role>> GetUserRolesAsync(long userId, CancellationToken ct = default);

    /// <summary>重绑账号角色：先删后插，原子执行。</summary>
    /// <param name="userId">后台账号 Id。</param>
    /// <param name="roleIds">目标角色 Id 集合，可为空表示解绑全部。</param>
    /// <param name="platformId">平台 Id，冗余存储。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> ReplaceUserRolesAsync(long userId, IReadOnlyCollection<long> roleIds, long platformId, CancellationToken ct = default);
    /// <summary>判断角色名是否已存在。</summary>
    /// <param name="roleName">角色名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>存在返回 true。幂等只读。</returns>
    Task<bool> ExistsByNameAsync(string roleName, CancellationToken ct = default);

    /// <summary>解绑某角色下的所有账号。删除角色时级联调用。</summary>
    /// <param name="roleId">角色 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> UnbindRoleAsync(long roleId, CancellationToken ct = default);

    /// <summary>软删角色。</summary>
    /// <param name="id">角色 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>判断角色编码是否已存在。</summary>
    /// <param name="code">角色编码。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>存在返回 true。幂等只读。</returns>
    Task<bool> ExistsByCodeAsync(string code, CancellationToken ct = default);

}

