using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace UserService.Application.Features.Internal;

/// <summary>后台账号凭据校验。仅供 AuthService 内部调用，不对外暴露。</summary>
/// <remarks>
/// 为什么把密码校验放在 UserService 而不是让 AuthService 取哈希自己比：
/// 密码哈希一旦离开账号服务，攻击面就从「一个内网接口」扩大到「哈希在网络里裸奔」——
/// 日志、抓包、内存转储都可能泄露。哈希留在本服务内，AuthService 只拿到「成 / 不成」。
/// 代价是登录多一次服务调用，用内网 HTTP 或 gRPC 均可。
/// </remarks>
public record AuthenticateAdminCommand(string UserName, string Password) : IRequest<ApiResponse<AdminIdentity>>;

/// <summary>校验通过后的后台账号身份。刻意不含密码哈希，也不含角色。</summary>
public record AdminIdentity(
    long UserId,
    string UserName,
    string NickName,
    string Avatar,
    int TenantType,
    long PlatformId,
    long MerchantId);

/// <summary>后台账号校验命令的校验器注册。</summary>
public static class AuthenticateAdminValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddAuthenticateAdminValidators(IServiceCollection services)
        => services.AddScoped<IValidator<AuthenticateAdminCommand>, AuthenticateAdminValidator>();

    /// <summary>登录入参校验。</summary>
    /// <remarks>
    /// 这里只校验「形状」（非空、长度上限），不校验密码强度：
    /// 强度是建号时的约束。老账号可能是历史弱密码，登录时被强度规则挡住会让用户永远登不进去。
    /// </remarks>
    private sealed class AuthenticateAdminValidator : AbstractValidator<AuthenticateAdminCommand>
    {
        /// <summary>构造校验器。</summary>
        public AuthenticateAdminValidator()
        {
            RuleFor(x => x.UserName).NotEmpty().Length(3, 64).WithMessage("请输入登录名");
            // 上限 128：挡住「超长密码」打满 PBKDF2 的拒绝服务，同时留足正常密码空间
            RuleFor(x => x.Password).NotEmpty().MaximumLength(128).WithMessage("请输入密码");
        }
    }
}