using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace PermissionService.Domain.Entities;

/// <summary>角色。AllowedScopes 与「全部权限」是正交的两件事。</summary>
/// <remarks>
/// 租户范围（AllowedScopes）决定「能管哪些平台/商户」，
/// 权限点集合决定「能调哪些接口」，两者互不替代（DATA_SPEC 5.21）。
/// 内置的 platform-admin / merchant-admin **禁止编辑权限、禁止删除**（BUSINESS 5.4）。
/// </remarks>
[Table(Name = "role")]
public class Role : EntityBase
{
    /// <summary>角色名，全局唯一。</summary>
    [Column(Name = "role_name", StringLength = 64)]
    public string RoleName { get; set; } = string.Empty;

    /// <summary>角色编码，全局唯一。内置角色用固定值 platform-admin / merchant-admin 等。</summary>
    [Column(Name = "role_code", StringLength = 64)]
    public string Code { get; set; } = string.Empty;

    /// <summary>租户范围：1 平台 / 2 商户 / 3 两者。</summary>
    [Column(Name = "allowed_scopes")]
    public int AllowedScopes { get; set; }

    /// <summary>数据范围：1 本级 / 2 本级及下级。</summary>
    [Column(Name = "data_scope")]
    public int DataScope { get; set; } = 1;

    /// <summary>状态：1 启用 / 2 停用。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = 1;

    /// <summary>是否内置角色。内置角色不可删除，权限集合锁定。</summary>
    [Column(Name = "is_builtin")]
    public bool IsBuiltin { get; set; }

    /// <summary>备注。</summary>
    [Column(Name = "remark", StringLength = 512)]
    public string Remark { get; set; } = string.Empty;
}

