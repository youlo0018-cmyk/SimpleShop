using Collaboration.Domain.Common;
using MediatR;
using PermissionService.Domain.IRepository;

namespace PermissionService.Application.Features.Internal;

/// <summary>角色与权限点解析处理器。</summary>
public sealed class ResolvePermissionsHandler
    : IRequestHandler<ResolvePermissionsCommand, ApiResponse<ResolvedPermissions>>
{
    private readonly IRoleRepository _roles;

    /// <summary>构造处理器。</summary>
    /// <param name="roles">角色仓储。</param>
    public ResolvePermissionsHandler(IRoleRepository roles) => _roles = roles;

    /// <summary>执行解析。</summary>
    /// <param name="request">解析命令，UserId 已由校验器保证为正数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回权限点与角色；账号无绑定时返回空集合（fail-closed，不报错）。</returns>
    public async Task<ApiResponse<ResolvedPermissions>> Handle(ResolvePermissionsCommand request, CancellationToken ct)
    {
        var permissions = await _roles.ResolvePermissionCodesAsync(request.UserId, ct);
        var roles = await _roles.GetUserRolesAsync(request.UserId, ct);

        var resolved = new ResolvedPermissions(
            request.UserId,
            permissions,
            roles
                .OrderBy(a => a.Code, StringComparer.Ordinal)
                .Select(a => new ResolvedRole(a.Id, a.RoleName, a.Code, a.AllowedScopes, a.DataScope))
                .ToArray());

        return ApiResults.Ok(resolved);
    }
}