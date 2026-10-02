using Collaboration.Domain.Common;
using MediatR;

namespace PermissionService.Application.Features.Permission.QueryTree;

/// <summary>查询权限树。仅后台权限管理使用，IncludeDisabled 控制是否带出已停用节点。</summary>
public record QueryPermissionTreeCommand(bool IncludeDisabled = false) : IRequest<ApiResponse<List<PermissionNodeDto>>>;

/// <summary>
/// 权限树节点。
/// Id：业务大类与模块有真实 Id，虚拟根「全部权限」为 0。
/// Name：中文名，直接展示，不显示编码（DESIGN_SPEC 6）。Code：仅叶子有值。
/// ApiPath：仅叶子有值。Level：1 业务大类 / 2 功能模块 / 3 权限点。
/// Status：1 启用 / 2 停用。IsBuiltin：内置权限点不可删除，只能停用。
/// Children：子节点。
/// </summary>
public record PermissionNodeDto(
    string Id,
    string Name,
    string? Code,
    string ApiPath,
    int Level,
    int Status,
    bool IsBuiltin,
    List<PermissionNodeDto> Children)
{
    /// <summary>
    /// 子树里是否存在可勾选的叶子。叶子以「有 code」判定，容器递归看子节点。
    /// </summary>
    public bool HasLeaf
        => Children.Count == 0 ? !string.IsNullOrEmpty(Code) : Children.Any(c => c.HasLeaf);

    /// <summary>
    /// 是否可勾选。
    /// </summary>
    /// <remarks>
    /// 虚拟根「全部权限」恒为 true（它代表全部叶子）；其余节点只要子树里有叶子就为 true。
    /// 空模块（例如品牌复用 product:* 后无独立权限点）为 false，前端据此禁止勾选，
    /// 避免出现「勾了却什么都没选」的困惑。
    /// </remarks>
    public bool Selectable => Level == 0 || HasLeaf;
}


