using Collaboration.Domain.Common;
using MediatR;
using PermissionService.Domain.Entities;
using PermissionService.Domain.IRepository;
// 命名空间段 Features.Permission 会遮蔽同名实体 Permission（CS0118），
// 按 CODING_STANDARD.md 陷阱 1 的规定用别名绕开。
using PermissionEntity = PermissionService.Domain.Entities.Permission;

namespace PermissionService.Application.Features.Permission.QueryTree;

/// <summary>权限树查询处理器：把扁平权限点拼成 4 层树，并在最外层套虚拟根节点「全部权限」。</summary>
/// <remarks>
/// 「全部权限」是**虚拟节点，不落库**（Id = 0），对应前端的「全选」按钮：
/// 勾上它等价于勾中全部叶子权限点，存储上仍然是逐条叶子记录，
/// 不引入 `*` 这种特殊权限值（DATA_SPEC 5.22）。
/// </remarks>
public sealed class QueryPermissionTreeHandler
    : IRequestHandler<QueryPermissionTreeCommand, ApiResponse<List<PermissionNodeDto>>>
{
    /// <summary>虚拟根节点「全部权限」的固定 Id。</summary>
    public const string AllNodeId = "0";

    private readonly IPermissionRepository _permissions;

    /// <summary>构造处理器。</summary>
    /// <param name="permissions">权限点仓储。</param>
    public QueryPermissionTreeHandler(IPermissionRepository permissions) => _permissions = permissions;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>4 层权限树，最外层是单个「全部权限」虚拟节点。</returns>
    public async Task<ApiResponse<List<PermissionNodeDto>>> Handle(QueryPermissionTreeCommand request, CancellationToken ct)
    {
        var all = await _permissions.QueryAllAsync(!request.IncludeDisabled, ct);
        var nodes = all.ToDictionary(a => a.Id, Build);

        var roots = all.Where(a => a.ParentId == 0).OrderBy(a => a.SortOrder).ThenBy(a => a.Id);
        var rootChildren = new List<PermissionNodeDto>();

        foreach (var node in roots)
        {
            var built = nodes[node.Id];
            AttachChildren(built, all, nodes);
            rootChildren.Add(built);
        }

        var tree = new List<PermissionNodeDto>
        {
            new(AllNodeId, "全部权限", null, string.Empty, 0, 1, true, rootChildren)
        };

        return ApiResults.Ok(tree);
    }

    private static void AttachChildren(PermissionNodeDto parent, List<PermissionEntity> all, Dictionary<long, PermissionNodeDto> nodes)
    {
        var parentId = long.Parse(parent.Id);
        var children = all.Where(a => a.ParentId == parentId)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id);

        foreach (var child in children)
        {
            if (!nodes.TryGetValue(child.Id, out var built)) continue;
            AttachChildren(built, all, nodes);
            parent.Children.Add(built);
        }
    }

    private static PermissionNodeDto Build(PermissionEntity p) => new(
        p.Id.ToString(),
        p.Name,
        string.IsNullOrEmpty(p.Code) ? null : p.Code,
        p.ApiPath,
        p.Level,
        p.Status,
        p.IsBuiltin,
        new List<PermissionNodeDto>());
}

