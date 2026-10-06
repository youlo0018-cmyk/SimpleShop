using Collaboration.Domain.Common;
using MediatR;
using PermissionService.Application.Features.Role;
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
    /// <para>绑定前校验每个角色的 AllowedScopes 与目标账号的租户类型匹配：
    /// 平台角色（1）不能绑给商户账号，商户角色（2）不能绑给平台账号，3 两者皆可。
    /// 这条规则放在这里而不是 UserService，是因为角色表在权限中心。</para>
    /// </remarks>
    public async Task<ApiResponse<int>> Handle(BindUserRolesCommand request, CancellationToken ct)
    {
        var roleIds = request.RoleIds ?? Array.Empty<long>();

        if (roleIds.Count > 0)
        {
            var roles = await _roles.GetByIdsAsync(roleIds, ct);

            var error = RoleScopeRules.Validate(roles, roleIds.Distinct().Count(), request.TenantType);
            if (error is not null)
            {
                return ApiResults.Fail<int>(BaseApiResponseCode.BadRequest, error);
            }
        }

        int affected = await _roles.ReplaceUserRolesAsync(
            request.UserId, roleIds, request.PlatformId, ct);

        return ApiResults.Ok(affected, "角色已绑定");
    }
}
