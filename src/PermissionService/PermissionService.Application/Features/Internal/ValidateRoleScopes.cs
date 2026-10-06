using Collaboration.Domain.Common;
using MediatR;
using PermissionService.Application.Features.Role;
using PermissionService.Domain.IRepository;

namespace PermissionService.Application.Features.Internal;

/// <summary>预检角色集合能否用于指定租户类型的账号。仅供 UserService 建号 / 改号前调用。</summary>
/// <remarks>
/// 为什么要在建号**之前**单独预检一次，而不是等绑定失败再报错：
/// 建号是「先插账号、再调权限中心绑角色」两步，绑定失败不会回滚账号
/// （见 HttpUserRoleClient 的说明）。如果只在绑定那一步校验，
/// 请求会返回「创建成功」而账号其实一个角色都没有 —— 运营看到的是成功，
/// 实际拿到的账号登进去什么都做不了。预检把这类请求挡在插库之前。
/// </remarks>
/// <param name="RoleIds">目标角色 Id 集合。</param>
/// <param name="TenantType">目标账号租户类型，1 平台 / 2 商户。</param>
public record ValidateRoleScopesCommand(
    IReadOnlyCollection<long> RoleIds,
    int TenantType) : IRequest<ApiResponse<string>>;

/// <summary>角色作用域预检处理器。</summary>
public sealed class ValidateRoleScopesHandler : IRequestHandler<ValidateRoleScopesCommand, ApiResponse<string>>
{
    private readonly IRoleRepository _roles;

    /// <summary>构造处理器。</summary>
    /// <param name="roles">角色仓储。</param>
    public ValidateRoleScopesHandler(IRoleRepository roles) => _roles = roles;

    /// <summary>执行预检。</summary>
    /// <param name="request">预检命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>通过返回成功（data 为空串）；不通过返回 400 与原因。</returns>
    public async Task<ApiResponse<string>> Handle(ValidateRoleScopesCommand request, CancellationToken ct)
    {
        var roleIds = request.RoleIds ?? Array.Empty<long>();
        if (roleIds.Count == 0) return ApiResults.Ok(string.Empty);

        var roles = await _roles.GetByIdsAsync(roleIds, ct);
        var error = RoleScopeRules.Validate(roles, roleIds.Distinct().Count(), request.TenantType);

        return error is null
            ? ApiResults.Ok(string.Empty)
            : ApiResults.Fail<string>(BaseApiResponseCode.BadRequest, error);
    }
}
