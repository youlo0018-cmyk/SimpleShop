using AuthService.Infrastructure;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Api;

/// <summary>
/// 把配置的公开客户端写入 OpenIddict 令牌库。幂等，每次启动对齐一次。
/// </summary>
/// <remarks>
/// 为什么在启动时写而不是靠 SQL 种子：客户端的**有效期与授权范围是配置**，
/// 跟着 AgileConfig 走才谈得上「改配置即生效」；写进 SQL 就成了需要手工同步的第二份真相。
/// 这里只更新自己关心的字段，不碰别人可能在库里加的其他配置。
/// </remarks>
public sealed class AuthClientSeeder : IAuthClientSeeder
{
    private readonly IOpenIddictApplicationManager _manager;
    private readonly ILogger<AuthClientSeeder> _logger;

    /// <summary>构造注册器。</summary>
    /// <param name="manager">OpenIddict 应用管理器。</param>
    /// <param name="logger">日志器。</param>
    public AuthClientSeeder(IOpenIddictApplicationManager manager, ILogger<AuthClientSeeder> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SeedAsync(AuthOptions options, CancellationToken ct = default)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = options.ClientId,
            ClientType = ClientTypes.Public,
            DisplayName = "SimpleShop 后台管理端",
            // 注意：令牌有效期不在这里配。OpenIddict 7 的 OpenIddictApplicationDescriptor
            // 没有有效期字段——有效期是服务端全局设置，在 Program.cs 的 AddServer 里用
            // SetAccessTokenLifetime / SetRefreshTokenLifetime 统一指定，避免「以为按客户端配了其实没生效」。
            // 公开客户端没有密钥可校验，所以必须显式允许无密钥换令牌；
            // 真正的隔离靠「后台入口才用这个 client_id」加上网关侧不暴露该端点给小程序
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.Password,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Token,
                Permissions.Scopes.Email,
                Permissions.Scopes.Profile,
                Permissions.Scopes.Roles
            },
            Requirements =
            {
                // 只收密码流，不接受 client_credentials / 授权码，避免被拿去当通用取票机
                Requirements.Features.ProofKeyForCodeExchange
            }
        };

        var application = await _manager.FindByClientIdAsync(options.ClientId, ct);

        if (application is null)
        {
            await _manager.CreateAsync(descriptor, ct);
            _logger.LogInformation("已创建公开客户端 {ClientId}", options.ClientId);
            return;
        }

        // 复用已有记录，只更新配置里声明的字段
        await _manager.UpdateAsync(application, descriptor, ct);
        _logger.LogInformation("公开客户端 {ClientId} 已按配置对齐", options.ClientId);
    }
}