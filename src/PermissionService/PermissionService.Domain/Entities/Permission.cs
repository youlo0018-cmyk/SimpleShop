using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace PermissionService.Domain.Entities;

/// <summary>权限点。既是鉴权单位，也是权限树的叶子节点。</summary>
/// <remarks>
/// 权限树固定 4 层（DATA_SPEC 5.4 / BUSINESS 5.4）：
/// 第 0 层「全部权限」是**虚拟根节点**，不落库，由前端渲染成勾选全部的按钮；
/// 第 1 层业务大类（5 个，parent_id = 0）；第 2 层功能模块（23 个）；第 3 层权限点（叶子）。
/// 依据：DATA_SPEC.md 5.21、5.22。
/// </remarks>
[Table(Name = "permission")]
public class Permission : EntityBase
{
    /// <summary>中文名，如「用户列表」。同一父节点下唯一。</summary>
    [Column(Name = "name", StringLength = 64)]
    public string Name { get; set; } = string.Empty;

    /// <summary>权限编码，格式 module:action，如 user:read。全局唯一。</summary>
    [Column(Name = "code", StringLength = 64)]
    public string Code { get; set; } = string.Empty;

    /// <summary>绑定的接口路径，多个用逗号分隔。必须以 /gateway/ 开头。</summary>
    [Column(Name = "api_path", StringLength = 512)]
    public string ApiPath { get; set; } = string.Empty;

    /// <summary>父节点 Id。0 表示一级（业务大类）。</summary>
    [Column(Name = "parent_id")]
    public long ParentId { get; set; }

    /// <summary>树层级：1 业务大类 / 2 功能模块 / 3 权限点。服务端按父链计算并校验。</summary>
    [Column(Name = "level")]
    public int Level { get; set; }

    /// <summary>同级排序，小的在前。</summary>
    [Column(Name = "sort_order")]
    public int SortOrder { get; set; }

    /// <summary>状态：1 启用 / 2 停用。停用后网关不再校验该权限点绑定的路径。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = 1;

    /// <summary>是否内置权限点。内置的**不可删除，只能停用**（BUSINESS 5.4）。</summary>
    [Column(Name = "is_builtin")]
    public bool IsBuiltin { get; set; }

    /// <summary>说明。</summary>
    [Column(Name = "description", StringLength = 200)]
    public string Description { get; set; } = string.Empty;
}

