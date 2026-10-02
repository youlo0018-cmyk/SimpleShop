using Collaboration.Domain.Configuration;
using Collaboration.Domain.Infrastructure;
using Collaboration.Web;
using Gateway.Api;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;

var builder = WebApplication.CreateBuilder(args);

// ---------- S1~S3：配置源。网关同样先读 AgileConfig，再取路由与下游地址 ----------
var environment = builder.Environment.EnvironmentName;
var loaded = await ServiceBootstrap.LoadConfigurationAsync(
    builder.Configuration, "Gateway", environment,
    extraRequiredKeys: new List<string> { "Consul:ServiceName" },
    // 网关不连数据库。显式豁免比填一个假的连接串诚实——
    // 否则后来的人看到「网关有数据库连接串」会以为它真的连了库。
    exemptBaseKeys: new List<string> { ConfigurationValidator.DatabaseConnectionKey });

builder.Configuration.AddConfiguration(
    new ConfigurationBuilder().AddInMemoryCollection(loaded).Build());

// 路由表来自随代码发布的 ocelot.json：Ocelot 要的是 Routes 数组，
// AgileConfig 只能存扁平键值对，表达不了这个结构。
builder.Configuration.AddJsonFile("ocelot.json", optional: false, reloadOnChange: true);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.SectionName));

// 权限中心客户端：只用来拉「路径 → 权限点」映射
builder.Services.AddHttpClient<RoutePermissionCache>((sp, http) =>
{
    var url = sp.GetRequiredService<IOptions<GatewayOptions>>().Value.Rbac.PermissionServiceUrl;
    if (string.IsNullOrWhiteSpace(url))
    {
        throw new InvalidOperationException(
            "缺少配置 Gateway:Rbac:PermissionServiceUrl。网关没有它就做不了 RBAC，请先补 AgileConfig。");
    }

    http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddSingleton<DualTokenValidator>();

// Ocelot：只做路由转发。
// 刻意不接 Polly：下游各服务的重试 / 降级还没做，先由服务自身保证幂等，
// 网关这一层过早加自动重试反而会把写操作重放一遍（幂等性尚未全面验证的阶段尤其危险）。
builder.Services.AddOcelot(builder.Configuration);

builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());

var app = builder.Build();

// 顺序：异常 → 安全（剥头 / 验签 / RBAC / 注头）→ 路由转发。
// 顺序不能换：必须先剥掉入站的租户头再做任何判断，否则就是给伪造开门。
app.UseAppExceptionHandling();
app.UseMiddleware<GatewaySecurityMiddleware>();
app.MapHealthChecks("/health");

// Ocelot 是**终结性**中间件：匹配不到路由就直接返回 404，不会往下传给端点。
// 所以 /health 必须绕过它，否则健康检查会被 Ocelot 判成「没有对应路由」。
// 探活与业务路由在这里分开：探活给编排系统看，业务才进网关链路。
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/health")
             && !context.Request.Path.StartsWithSegments("/ready"),
    branch => branch.UseOcelot().Wait());

app.Run();
