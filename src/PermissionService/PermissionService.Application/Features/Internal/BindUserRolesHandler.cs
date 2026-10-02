using Collaboration.Domain.Common;
using MediatR;
using PermissionService.Domain.IRepository;

namespace PermissionService.Application.Features.Internal;

/// <summary>重绑账号角色处理器。</summary>
public sealed class BindUserRolesHandler : IRequestHandler<BindUserRolesCommand, ApiResponse<int>>
{
    private readonly IRoleRepository _roles;

    /// <summary>构造处理器。</summary>
    /// <param name="roles">角色仓储。</param>
    public BindUserRolesHandler(IRoleRepository roles) => _roles = roles;

    /// <summary>执行重绑。</summary>
    /// <param name="request">绑定命令，形状已由校验器保证。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回受影响行数。</returns>
    /// <remarks>
    /// 先删后插在仓储里以事务完成，所以「目标集合为空」就是解绑全部，不会留下半截状态。
    /// 停用中的角色允许绑定：角色可能被停用后恢复，绑定的意图应当被记住。
    /// </remarks>
    public async Task<ApiResponse<int>> Handle(BindUserRolesCommand request, CancellationToken ct)
    {
        int affected = await _roles.ReplaceUserRolesAsync(
            request.UserId, request.RoleIds ?? Array.Empty<long>(), request.PlatformId, ct);

        return ApiResults.Ok(affected, "角色已绑定");
    }
}