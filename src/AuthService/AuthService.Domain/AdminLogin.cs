namespace AuthService.Domain;

/// <summary>后台账号凭据校验的结果。</summary>
public enum AdminLoginOutcome
{
    /// <summary>校验通过。</summary>
    Success = 0,

    /// <summary>登录名或密码错误。<b>与「账号不存在」返回同一结果</b>，防止账号枚举。</summary>
    InvalidCredentials = 1,

    /// <summary>账号已停用。</summary>
    Disabled = 2,

    /// <summary>下游账号服务不可用。</summary>
    AccountServiceUnavailable = 3
}

/// <summary>登录成功后要写进令牌的身份信息。</summary>
public sealed record AdminPrincipal(
    long UserId,
    string UserName,
    string NickName,
    int TenantType,
    long PlatformId,
    long MerchantId,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Roles);

/// <summary>凭据校验与权限解析的端口。实现在 Infrastructure 层（走内网 HTTP 调下游服务）。</summary>
public interface IAdminIdentityResolver
{
    /// <summary>校验后台账号凭据。</summary>
    /// <param name="userName">登录名。</param>
    /// <param name="password">明文密码。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>校验结果。</returns>
    Task<(AdminLoginOutcome Outcome, AdminPrincipal? Principal)> AuthenticateAsync(
        string userName, string password, CancellationToken ct = default);

    /// <summary>解析账号的角色与权限点。</summary>
    /// <param name="userId">后台账号 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>权限点编码与角色编码集合；无绑定时都是空（fail-closed）。</returns>
    Task<(IReadOnlyList<string> Permissions, IReadOnlyList<string> Roles)> ResolvePermissionsAsync(
        long userId, CancellationToken ct = default);
}