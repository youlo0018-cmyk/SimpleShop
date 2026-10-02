using FreeSql.DataAnnotations;

namespace PermissionService.Domain.Entities;

/// <summary>后台账号与角色的绑定关系。</summary>
/// <remarks>
/// 纯关联表，复合主键 (user_id, role_id)。
/// 它是**权限的唯一来源**：账号表上不得有任何角色字段兜底，
/// 无绑定即无权限（fail-closed，BUSINESS 5.3）。
/// </remarks>
[Table(Name = "user_role")]
public class UserRole
{
    /// <summary>后台账号 Id（UserService 的 User.Id），雪花 Id。</summary>
    [Column(Name = "user_id", IsPrimary = true)]
    public long UserId { get; set; }

    /// <summary>角色 Id，雪花 Id。</summary>
    [Column(Name = "role_id", IsPrimary = true)]
    public long RoleId { get; set; }

    /// <summary>创建时间，UTC。</summary>
    [Column(Name = "created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>平台 Id，冗余存储用于按平台裁剪。</summary>
    [Column(Name = "platform_id")]
    public long PlatformId { get; set; }
}

