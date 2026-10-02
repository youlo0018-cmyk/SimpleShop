using FreeSql.DataAnnotations;

namespace PermissionService.Domain.Entities;

/// <summary>角色与权限点的绑定关系。</summary>
/// <remarks>
/// 纯关联表，不继承 EntityBase：它没有雪花主键，用 (role_id, permission_id) 复合主键。
/// 「重绑权限」按物理删除后重建实现，因为复合唯一约束不支持原地更新（DATA_SPEC 5.21）。
/// </remarks>
[Table(Name = "role_permission")]
public class RolePermission
{
    /// <summary>角色 Id，雪花 Id。</summary>
    [Column(Name = "role_id", IsPrimary = true)]
    public long RoleId { get; set; }

    /// <summary>权限点 Id，雪花 Id。</summary>
    [Column(Name = "permission_id", IsPrimary = true)]
    public long PermissionId { get; set; }

    /// <summary>创建时间，UTC。</summary>
    [Column(Name = "created_at")]
    public DateTime CreatedAt { get; set; }
}

