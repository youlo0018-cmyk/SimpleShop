using Collaboration.Domain.Common;
using MediatR;
using PermissionService.Application.Services;
using PermissionService.Domain.IRepository;

namespace PermissionService.Application.Features.Permission.QueryTree;

/// <summary>权限树查询处理器：取全量权限点后交给纯函数拼树。</summary>
/// <remarks>
/// 树结构规则全部在 <see cref="PermissionTreeBuilder"/> 里，那是纯函数、可单测；
/// 本类只负责取数与包装响应。
/// </remarks>
public sealed class QueryPermissionTreeHandler
    : IRequestHandler<QueryPermissionTreeCommand, ApiResponse<List<PermissionNodeDto>>>
{
    private readonly IPermissionRepository _permissions;

    /// <summary>构造处理器。</summary>
    /// <param name="permissions">权限点仓储。</param>
    public QueryPermissionTreeHandler(IPermissionRepository permissions) => _permissions = permissions;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>4 层权限树，最外层是单个虚拟根节点「全部权限」。</returns>
    public async Task<ApiResponse<List<PermissionNodeDto>>> Handle(QueryPermissionTreeCommand request, CancellationToken ct)
    {
        var permissions = await _permissions.QueryAllAsync(!request.IncludeDisabled, ct);
        return ApiResults.Ok(PermissionTreeBuilder.Build(permissions));
    }
}

