using PermissionService.Application.Features.Permission.QueryTree;
using PermissionEntity = PermissionService.Domain.Entities.Permission;

namespace PermissionService.Application.Services;

/// <summary>把扁平权限点拼成 4 层权限树。纯函数，无依赖，可单测。</summary>
/// <remarks>
/// 树结构（DATA_SPEC 5.22 / BUSINESS 5.4）：
/// 第 0 层「全部权限」是**虚拟根节点**，Id 固定为 0，不落库，
/// 对应前端的「全选」按钮；勾上它等价于勾中全部叶子，存储上仍逐条记录叶子，不引入 `*` 特殊值。
/// 第 1 层业务大类（parent_id = 0）、第 2 层功能模块、第 3 层权限点（叶子）。
/// 空模块（例如品牌复用 product:* 后没有独立权限点）**会保留节点但 Selectable = false**，
/// 前端据此禁止勾选，避免出现「勾了却什么都没选」的困惑。
/// </remarks>
public static class PermissionTreeBuilder
{
    /// <summary>虚拟根节点「全部权限」的固定 Id。</summary>
    public const string AllNodeId = "0";

    /// <summary>虚拟根节点的名称，前端直接展示。</summary>
    public const string AllNodeName = "全部权限";

    /// <summary>虚拟根节点的层级，0 表示虚拟层（真实节点从 1 开始）。</summary>
    public const int AllNodeLevel = 0;

    /// <summary>
    /// 构建权限树。
    /// </summary>
    /// <param name="permissions">全部未删除的权限点，可乱序。</param>
    /// <returns>只含一个元素（虚拟根）的列表。</returns>
    public static List<PermissionNodeDto> Build(IReadOnlyCollection<PermissionEntity> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var nodes = new Dictionary<long, PermissionNodeDto>();
        foreach (var p in permissions) nodes[p.Id] = Map(p);

        var rootChildren = new List<PermissionNodeDto>();
        foreach (var top in Order(permissions, p => p.ParentId == 0))
        {
            var node = nodes[top.Id];
            Attach(node, permissions, nodes);
            rootChildren.Add(node);
        }

        var all = new PermissionNodeDto(
            AllNodeId, AllNodeName, null, string.Empty, AllNodeLevel, 1, true, rootChildren);
        return new List<PermissionNodeDto> { all };
    }

    private static void Attach(
        PermissionNodeDto parent,
        IReadOnlyCollection<PermissionEntity> all,
        Dictionary<long, PermissionNodeDto> nodes)
    {
        foreach (var child in Order(all, p => p.ParentId == parent.LongId()))
        {
            var node = nodes[child.Id];
            Attach(node, all, nodes);
            parent.Children.Add(node);
        }
    }

    private static IEnumerable<PermissionEntity> Order(
        IReadOnlyCollection<PermissionEntity> all,
        Func<PermissionEntity, bool> predicate)
        => all.Where(predicate).OrderBy(p => p.SortOrder).ThenBy(p => p.Id);

    private static PermissionNodeDto Map(PermissionEntity p) => new(
        p.Id.ToString(),
        p.Name,
        string.IsNullOrEmpty(p.Code) ? null : p.Code,
        p.ApiPath,
        p.Level,
        p.Status,
        p.IsBuiltin,
        new List<PermissionNodeDto>());

    private static long LongId(this PermissionNodeDto node) => long.Parse(node.Id);
}

