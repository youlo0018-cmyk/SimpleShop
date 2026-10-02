using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace PermissionService.Application.Features.Internal;

/// <summary>重绑后台账号的角色集合。仅供 UserService 建号 / 改号时调用。</summary>
/// <remarks>
/// 绑定只认显式调用：账号表上没有任何角色字段，也不做「没绑就默认给个角色」的兜底，
/// 否则 fail-closed 的前提（权限只来自 user_role）就破了（BUSINESS 5.3）。
/// </remarks>
public record BindUserRolesCommand(
    long UserId,
    IReadOnlyCollection<long> RoleIds,
    long PlatformId = 0) : IRequest<ApiResponse<int>>;

/// <summary>绑定命令的校验器注册。</summary>
public static class BindUserRolesValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddBindUserRolesValidators(IServiceCollection services)
        => services.AddScoped<IValidator<BindUserRolesCommand>, BindUserRolesValidator>();

    /// <summary>入参校验。</summary>
    private sealed class BindUserRolesValidator : AbstractValidator<BindUserRolesCommand>
    {
        /// <summary>构造校验器。</summary>
        public BindUserRolesValidator()
        {
            RuleFor(x => x.UserId).GreaterThan(0).WithMessage("账号 Id 必须为正数");
            // RoleIds 允许为空：那是「解绑全部」，是合法的管理动作
            RuleFor(x => x.RoleIds).NotNull().WithMessage("角色集合不能为 null（解绑全部请传空数组）");
        }
    }
}