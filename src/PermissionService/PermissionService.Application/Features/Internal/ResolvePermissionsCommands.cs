using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace PermissionService.Application.Features.Internal;

/// <summary>
/// 解析某后台账号的角色与权限点。仅供 AuthService 登录时调用，网关不路由 /internal 前缀。
/// </summary>
/// <remarks>
/// 权限的唯一来源是 user_role 绑定表（BUSINESS 5.3 fail-closed）：
/// 账号上没有角色字段，也没有「默认给个角色」的兜底——无绑定就是无权限，
/// 返回空集合而不是报错，让 AuthService 照样签发一个「登录成功但什么都不能做」的令牌。
/// </remarks>
public record ResolvePermissionsCommand(long UserId) : IRequest<ApiResponse<ResolvedPermissions>>;

/// <summary>解析结果。权限点为编码集合，角色带上名称便于前端直接显示。</summary>
public record ResolvedPermissions(
    long UserId,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<ResolvedRole> Roles);

/// <summary>角色摘要。</summary>
public record ResolvedRole(long RoleId, string RoleName, string Code, int AllowedScopes, int DataScope);

/// <summary>解析命令的校验器注册。</summary>
public static class ResolvePermissionsValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddResolvePermissionsValidators(IServiceCollection services)
        => services.AddScoped<IValidator<ResolvePermissionsCommand>, ResolvePermissionsValidator>();

    /// <summary>入参校验。</summary>
    private sealed class ResolvePermissionsValidator : AbstractValidator<ResolvePermissionsCommand>
    {
        /// <summary>构造校验器。</summary>
        public ResolvePermissionsValidator()
        {
            RuleFor(x => x.UserId).GreaterThan(0).WithMessage("账号 Id 必须为正数");
        }
    }
}