using System.Security.Claims;
using AuthService.Domain;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Api;

/// <summary>密码流的凭据校验阶段：校验客户端与账号，把验好的身份交给控制器签发。</summary>
/// <remarks>
/// <para>公开客户端（admin-app）用 password flow：本项目的前端是纯浏览器 / 小程序，放不下 client secret，
/// 所以客户端不校验密钥，靠「只有后台入口会拿这个令牌」来隔离（BUSINESS 4.1）。
/// 代价是 access token 必须当 bearer token 用，且有效期要短（默认 2 小时）。</para>
///
/// <para>客户账号走这个入口必须拿到 invalid_grant：客户在 CustomerService 的库里，
/// UserService 根本查不到这个人，落到 InvalidCredentials 分支，天然互斥（BUSINESS 4.2）。</para>
///
/// <para>权限是<b>登录那一刻</b>解析进令牌的。管理员改了角色权限不影响已登录会话，要重新登录才生效——
/// 这是文档明确要的语义（BUSINESS 5.4「生效时机」）。</para>
///
/// <para><b>拒绝原因必须是 ASCII</b>：OpenIddict 会把 Reject 的 description 原样写进
/// WWW-Authenticate 响应头，HTTP 头里出现非 ASCII 字符会被 Kestrel 直接拒绝、
/// 整个登录变成 500。踩过：写中文提示导致登录接口 500 且只在服务端日志里留痕。
/// 中文提示由前端按 OAuth2 的 error 码（invalid_grant 等）映射，这也是 OAuth2 惯例。</para>
///
/// <para><b>不要在这里调 context.HandleRequest()</b>。开了 EnableTokenEndpointPassthrough 之后，
/// 令牌由 TokenController 用 SignIn(...) 签发；这里调 HandleRequest 等于告诉 OpenIddict
/// 「整个令牌请求已经处理完」，它会直接收尾并返回空响应 200。踩过：200 但 body 长度为 0。</para>
/// </remarks>
public sealed class ValidateAdminPasswordGrantHandler
    : IOpenIddictServerHandler<OpenIddictServerEvents.ValidateTokenRequestContext>
{
    /// <summary>校验通过的主体在 Transaction.Properties 里的键名。</summary>
    /// <remarks>
    /// 用 HttpContext.Items 而不是 Transaction.Properties：开了 passthrough 之后令牌由控制器签发，
    /// 不再有第二个 OpenIddict 处理器参与。校验阶段与控制器拿到的是同一个 HttpContext，Items 天然共享。
    /// 两个地方共用这一个常量，避免写错字符串后静默对不上。
    /// </remarks>
    internal const string PrincipalKey = "SimpleShop.AdminPrincipal";

    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IAdminIdentityResolver _resolver;
    private readonly ILogger<ValidateAdminPasswordGrantHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="applicationManager">OpenIddict 客户端管理器。</param>
    /// <param name="resolver">账号与权限解析器。</param>
    /// <param name="logger">日志器。</param>
    public ValidateAdminPasswordGrantHandler(
        IOpenIddictApplicationManager applicationManager,
        IAdminIdentityResolver resolver,
        ILogger<ValidateAdminPasswordGrantHandler> logger)
    {
        _applicationManager = applicationManager;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>处理器描述符。</summary>
    /// <remarks>
    /// 排在客户端校验之后：必须先确认这个 client_id 存在、且允许密码流，再去校验用户凭据。
    /// 反过来的话，任意 client_id 都能把凭据校验打一遍，
    /// 等于给账号服务开了一个不需要合法客户端的探测口。
    /// </remarks>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor
            .CreateBuilder<OpenIddictServerEvents.ValidateTokenRequestContext>()
            .UseScopedHandler<ValidateAdminPasswordGrantHandler>()
            .SetOrder(OpenIddictServerHandlers.ValidateClientId.Descriptor.Order + 1_000)
            .SetType(OpenIddictServerHandlerType.BuiltIn)
            .Build();

    /// <summary>执行校验阶段。</summary>
    /// <param name="context">令牌请求校验上下文。</param>
    /// <returns>异步任务。</returns>
    public async ValueTask HandleAsync(OpenIddictServerEvents.ValidateTokenRequestContext context)
    {
        // 只管密码流。授权码、刷新令牌等交给 OpenIddict 内置处理器，别越界。
        if (!string.Equals(context.Request.GrantType, GrantTypes.Password, StringComparison.Ordinal)) return;

        var request = context.Request;

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            // 参数缺失与凭据错误都回 invalid_grant，不告诉调用方到底缺了什么
            context.Reject(error: Errors.InvalidGrant, description: "Invalid username or password.");
            return;
        }

        // FindByClientIdAsync 的声明返回类型是 object（OpenIddict 要兼容 EF Core 之外的多种存储）。
        // 实测它返回的是 EF Core 的实体本身 OpenIddictEntityFrameworkCoreApplication，
        // 不是 OpenIddictApplicationDescriptor —— 早先误转成描述符，as 静默返回 null，
        // 于是每个合法客户端都被报成「Unknown client」，登录永远走不通。
        var application = await _applicationManager.FindByClientIdAsync(
            request.ClientId ?? string.Empty, context.CancellationToken)
            as OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreApplication;

        if (application is null)
        {
            context.Reject(error: Errors.InvalidClient, description: "Unknown client.");
            return;
        }

        // 公开客户端必须显式允许密码流。这里再挡一次，别只依赖注册时的配置——
        // 万一有人往库里塞了个允许 authorization_code 的客户端，密码校验不该被顺带打开。
        // Permissions 可能为 null（客户端一条权限都没配），?. 让它自然变成「不允许」，语义也对。
        var isPublicClient = string.Equals(application.ClientType, ClientTypes.Public, StringComparison.Ordinal);
        if (!isPublicClient || application.Permissions?.Contains(Permissions.GrantTypes.Password) != true)
        {
            _logger.LogWarning("客户端 {ClientId} 不允许 password 流，拒绝", request.ClientId);
            context.Reject(error: Errors.UnauthorizedClient, description: "This client is not allowed to use the password grant.");
            return;
        }

        var (outcome, principal) = await _resolver.AuthenticateAsync(
            request.Username, request.Password, context.CancellationToken);

        switch (outcome)
        {
            case AdminLoginOutcome.Success:
                break;

            case AdminLoginOutcome.Disabled:
                context.Reject(error: Errors.InvalidGrant, description: "The account has been disabled.");
                return;

            case AdminLoginOutcome.AccountServiceUnavailable:
                // 服务端故障不能伪装成密码错误，否则用户只会反复重试，真正的问题埋在日志里
                context.Reject(error: Errors.ServerError, description: "The authentication service is temporarily unavailable.");
                return;

            default:
                context.Reject(error: Errors.InvalidGrant, description: "Invalid username or password.");
                return;
        }

        // Outcome 是 Success 时主体必然非空，但编译器看不出这个关联。
        // 与其用 ! 糊过去，不如显式判空——真为 null 也是我们该知道的 bug，不该被掩盖。
        if (principal is null)
        {
            _logger.LogError("凭据校验返回了 Success 但没有主体，按服务端异常处理");
            context.Reject(error: Errors.ServerError, description: "The authentication service is temporarily unavailable.");
            return;
        }

        var (permissions, roles) = await _resolver.ResolvePermissionsAsync(
            principal.UserId, context.CancellationToken);

        context.Transaction.Properties[PrincipalKey] = BuildPrincipal(principal, permissions, roles);

        _logger.LogInformation(
            "后台登录凭据校验通过：账号 {UserName}({UserId})，权限 {PermissionCount} 项，角色 {RoleCount} 个",
            principal.UserName, principal.UserId, permissions.Count, roles.Count);
    }

    /// <summary>把身份与权限组装成 ClaimsPrincipal。</summary>
    /// <param name="principal">账号身份。</param>
    /// <param name="permissions">权限点编码集合。</param>
    /// <param name="roles">角色编码集合。</param>
    /// <returns>带完整声明的主体。</returns>
    private static ClaimsPrincipal BuildPrincipal(
        AdminPrincipal principal,
        IReadOnlyList<string> permissions,
        IReadOnlyList<string> roles)
    {
        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, principal.UserId.ToString());
        identity.SetClaim(Claims.Name, principal.UserName);
        identity.SetClaim(Claims.PreferredUsername, principal.UserName);
        identity.SetClaim(AdminTokenClaims.UserName, principal.UserName);
        identity.SetClaim(AdminTokenClaims.NickName, principal.NickName);
        identity.SetClaim(AdminTokenClaims.TenantType, principal.TenantType.ToString());
        identity.SetClaim(AdminTokenClaims.PlatformId, principal.PlatformId.ToString());
        identity.SetClaim(AdminTokenClaims.MerchantId, principal.MerchantId.ToString());

        // 这里必须用 AddClaim，**不能**用 SetClaim。
        // SetClaim 的语义是「先删掉同类型的所有声明再加一条」，循环里用它只会留下最后一个——
        // 78 个权限点会变成 1 个，而且接口照常返回 200、令牌照常用，
        // 直到网关按权限拦截时才暴露成「超级管理员什么都做不了」。
        // 踩过：令牌里只剩最后一个 permission，排查时才反应过来。
        foreach (var permission in permissions)
            identity.AddClaim(new Claim(AdminTokenClaims.Permission, permission));

        foreach (var role in roles)
            identity.AddClaim(new Claim(AdminTokenClaims.Role, role));

        // 权限类声明只进 access token。ID token 是给客户端看的，
        // 塞 78 个权限点只会让令牌变大、泄露面更宽，而网关只读 access token。
        identity.SetDestinations(static claim => claim.Type switch
        {
            Claims.Subject or Claims.Name or Claims.PreferredUsername or AdminTokenClaims.NickName
                => [Destinations.AccessToken, Destinations.IdentityToken],

            _ => [Destinations.AccessToken]
        });

        return new ClaimsPrincipal(identity);
    }
}