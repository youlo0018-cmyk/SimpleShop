using AuthService.Api;
using AuthService.Domain;
using AuthService.Infrastructure;
using Collaboration.Domain.Configuration;
using Collaboration.Domain.Infrastructure;
using Collaboration.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
// ---------- 常量（本文件多处引用，必须在使用前声明） ----------

// 指向 UserService 的具名 HttpClient 名
const string UserServiceClient = "UserService";

// 指向 PermissionService 的具名 HttpClient 名
const string PermissionServiceClient = "PermissionService";

// UserService 地址的配置键
const string UserServiceUrlKey = "Services:UserServiceBaseUrl";

// PermissionService 地址的配置键
const string PermissionServiceUrlKey = "Services:PermissionServiceBaseUrl";

// 下游调用的超时。账号与权限都是同步链路上的关键路径，
// 卡住比失败更糟——用户会一直转圈，不如快速失败让他重试。
var downstreamTimeout = TimeSpan.FromSeconds(5);

var builder = WebApplication.CreateBuilder(args);

// ---------- S1~S3：配置源。先读 AgileConfig，再取数据库等下游配置 ----------
var environment = builder.Environment.EnvironmentName;
var loaded = await ServiceBootstrap.LoadConfigurationAsync(
    builder.Configuration, "AuthService", environment, new List<string> { "Consul:ServiceName" });

builder.Configuration.AddConfiguration(
    new ConfigurationBuilder().AddInMemoryCollection(loaded).Build());

var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
    ?? throw new InvalidOperationException("缺少 Database 配置，配置校验被绕过了。");

var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>()
    ?? throw new InvalidOperationException("缺少 Auth 配置，配置校验被绕过了。");

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

// ---------- 令牌存储：EF Core + OpenIddict ----------
// AuthService 是全项目唯一用 EF Core 的服务，原因见 AuthDbContext 的注释。
// 这里没有业务表，因此不需要 FreeSql，也不需要雪花 Id 与 Redis 分配 workerId。
builder.Services.AddDbContext<AuthDbContext>(options =>
{
    options.UseNpgsql(databaseOptions.Default);
    options.UseOpenIddict();
});

// 下游地址一律来自配置，代码里不出现端口。
// 两个下游用两个具名 HttpClient，解析器从工厂各取一个，不会互相串地址。
builder.Services.AddHttpClient(UserServiceClient)
    .ConfigureHttpClient((sp, client) =>
    {
        client.BaseAddress = new Uri(Require(sp.GetRequiredService<IConfiguration>(), UserServiceUrlKey).TrimEnd('/') + "/");
        client.Timeout = downstreamTimeout;
    });

builder.Services.AddHttpClient(PermissionServiceClient)
    .ConfigureHttpClient((sp, client) =>
    {
        client.BaseAddress = new Uri(Require(sp.GetRequiredService<IConfiguration>(), PermissionServiceUrlKey).TrimEnd('/') + "/");
        client.Timeout = downstreamTimeout;
    });

builder.Services.AddSingleton<IAdminIdentityResolver>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new HttpAdminIdentityResolver(
        factory.CreateClient(UserServiceClient),
        factory.CreateClient(PermissionServiceClient),
        sp.GetRequiredService<ILogger<HttpAdminIdentityResolver>>());
});

// 登录中间状态：校验阶段写入，签发阶段读取（同一请求同一个作用域）
builder.Services.AddSingleton<SigningCertificateProvider>();
builder.Services.AddScoped<IAuthClientSeeder, AuthClientSeeder>();

var certificate = new SigningCertificateProvider(
    Options.Create(authOptions),
    LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SigningCertificateProvider>()).GetOrCreate();

builder.Services
    .AddOpenIddict()
    .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AuthDbContext>())
    .AddServer(options =>
    {
        options.SetIssuer(authOptions.Issuer);
        options.SetTokenEndpointUris("connect/token");

        // password flow 给公开客户端 admin-app 用；refresh flow 让前端续期而不必重输密码
        options.AllowPasswordFlow()
               .AllowRefreshTokenFlow();

        // RS256：OpenIddict 7 给 RSA 证书默认就用 RS256，没有「传算法」的重载。
        // 签发后必须核对令牌头的 alg 确实是 RS256（网关也只认 RS256），见 e2e 断言。
        options.AddSigningCertificate(certificate);

        // 即使禁用了访问令牌加密，OpenIddict 仍强制要求至少注册一把加密密钥
        // （启动时 PostConfigure 会直接抛 InvalidOperationException）。
        // 这里复用同一份证书：ID token 等仍可能走加密，多一把独立密钥只会让部署更麻烦。
        // 注意不能用 AddEphemeralEncryptionKey——重启后密钥就没了，
        // 重启前签发的加密令牌会集体失效。
        options.AddEncryptionCertificate(certificate);

        // 有效期是服务端全局设置：OpenIddict 7 的客户端描述符里没有有效期字段。
        // 公开客户端拿不到 client secret，令牌就是唯一凭证，所以 access token 必须短。
        options.SetAccessTokenLifetime(TimeSpan.FromHours(authOptions.AccessTokenHours))
               .SetIdentityTokenLifetime(TimeSpan.FromHours(authOptions.AccessTokenHours))
               .SetRefreshTokenLifetime(TimeSpan.FromDays(authOptions.RefreshTokenDays));

        // 网关要直接读令牌里的 tenant_type / permission 声明，不能是加密的 JWE
        options.DisableAccessTokenEncryption();

        // OpenIddict 不会自动发现处理器，必须显式注册，否则 password 流根本没有实现
        options.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(handler =>
            handler.UseScopedHandler<ValidateAdminPasswordGrantHandler>());

        options.UseAspNetCore()
               .EnableTokenEndpointPassthrough();

        // OpenIddict 默认只接受 HTTPS 请求（OAuth2 安全要求：口令不能走明文链路）。
        // 本地开发跑在 http://127.0.0.1 上，不放开就没法测。
        // 只在**非生产**环境放开：生产保持强制，万一有人误配成 HTTP，
        // 服务本身会拒绝而不是悄悄降级。
        // 注意这个方法挂在 UseAspNetCore() 返回的构建器上，不在 OpenIddictServerBuilder 上。
        if (!builder.Environment.IsProduction())
        {
            options.UseAspNetCore()
                   .DisableTransportSecurityRequirement();
        }
    })
    .AddValidation(options =>
    {
        // 验证端点没有 passthrough 的问题：它是给网关验签用的，
        // 不需要 MVC 控制器参与，凭据由 OpenIddict 自己解析。
        options.UseLocalServer();
        options.UseAspNetCore();
    });

builder.Services.AddControllers();
builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());

var app = builder.Build();

// 注册公开客户端。幂等：每次启动都对齐一次配置，改了 ClientId 不必手工改库。
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<IAuthClientSeeder>().SeedAsync(authOptions);
}

app.UseAppTenantContext();
app.UseAppExceptionHandling();
app.MapHealthChecks("/health");
app.MapControllers();
app.Run();

// 读取必填配置，缺失就快速失败
static string Require(IConfiguration configuration, string key)
{
    var value = configuration[key];
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException(
            $"缺少配置 {key}。请在 AgileConfig 补上后重试——没有下游地址就登录不了，这不是可以降级的配置。");
    }
    return value;
}