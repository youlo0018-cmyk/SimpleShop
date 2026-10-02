using PermissionService.Domain.Entities;

namespace PermissionService.Domain.IRepository;

/// <summary>权限点仓储。权限树与鉴权目录都从这里读。</summary>
public interface IPermissionRepository
{
    /// <summary>查询全部未删除权限点，按层级与排序返回，用于拼权限树。</summary>
    /// <param name="onlyEnabled">true 时只返回启用状态的权限点。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>权限点列表。幂等只读。</returns>
    Task<List<Permission>> QueryAllAsync(bool onlyEnabled, CancellationToken ct = default);

    /// <summary>按 Id 取权限点，不存在返回 null。</summary>
    /// <param name="id">权限点 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>权限点或 null。幂等只读。</returns>
    Task<Permission?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>判断权限编码是否已存在。</summary>
    /// <param name="code">权限编码。</param>
    /// <param name="excludeId">排除的 Id（编辑时传自身 Id），可为 0。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>存在返回 true。幂等只读。</returns>
    Task<bool> ExistsByCodeAsync(string code, long excludeId = 0, CancellationToken ct = default);

    /// <summary>判断同一父节点下中文名是否重复。</summary>
    /// <param name="name">中文名。</param>
    /// <param name="parentId">父节点 Id，0 为一级。</param>
    /// <param name="excludeId">排除的 Id，可为 0。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>重复返回 true。幂等只读。</returns>
    Task<bool> ExistsByNameAsync(string name, long parentId, long excludeId = 0, CancellationToken ct = default);

    /// <summary>取子节点。</summary>
    /// <param name="parentId">父节点 Id，0 表示取一级节点。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>子节点列表。幂等只读。</returns>
    Task<List<Permission>> GetChildrenAsync(long parentId, CancellationToken ct = default);

    /// <summary>插入权限点。</summary>
    /// <param name="permission">权限点实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新权限点 Id。非幂等。</returns>
    Task<long> InsertAsync(Permission permission, CancellationToken ct = default);

    /// <summary>按字段更新权限点。</summary>
    /// <param name="permission">携带 Id 与待更新字段的实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> UpdateAsync(Permission permission, CancellationToken ct = default);

    /// <summary>按 Id 数组查询权限点。</summary>
    /// <param name="ids">权限点 Id 集合，可为空。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的权限点列表。幂等只读。</returns>
    Task<List<Permission>> GetByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);
    /// <summary>软删权限点。</summary>
    /// <param name="id">权限点 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等：重复软删第二次返回 0。</returns>
    Task<int> DeleteAsync(long id, CancellationToken ct = default);

}

