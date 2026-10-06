using Collaboration.Domain.Configuration;
using Collaboration.Domain.Infrastructure;
using Collaboration.Web;
using Gateway.Api;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;
using StackExchange.Redis;

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

// Redis：网关只用它读「后台账号的会话吊销时刻」（DATA_SPEC 5.20）。
// 以前网关刻意不连 Redis，现在有了这条跨服务约定，连接是必需的：
// 读不到吊销状态就必须拒绝请求，所以 Redis 挂了等于后台整体不可用，这一点写进 /ready 探针。
var redisOptions = builder.Configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>()!;
var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions.ConnectionString);
Console.WriteLine($"[bootstrap] redis 已连接 db={redisOptions.SharedDatabase}（会话吊销共享库）");

builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
builder.Services.AddSingleton(sp => new AdminSessionRevocationChecker(
    redis, redisOptions.SharedDatabase, sp.GetRequiredService<ILogger<AdminSessionRevocationChecker>>()));

// Ocelot：只做路由转发。
// 刻意不接 Polly：下游各服务的重试 / 降级还没做，先由服务自身保证幂等，
// 网关这一层过早加自动重试反而会把写操作重放一遍（幂等性尚未全面验证的阶段尤其危险）。
builder.Services.AddOcelot(builder.Configuration);

// 两个探针，职责不同（下面 MapHealthChecks 处有对应说明）：
//   /health —— **存活**：只回答「进程还在不在」，不碰任何外部依赖。
//   /ready  —— **就绪**：额外探真实依赖，回答「现在能不能接流量」。
builder.Services
    .AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy())
    .AddCheck<GatewayHealthCheck>("gateway");

builder.Services.AddHttpClient("GatewayHealthProbe", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddAppEventLogging(builder.Configuration);

var app = builder.Build();

// 顺序：异常 → 安全（剥头 / 验签 / RBAC / 注头）→ 路由转发。
// 顺序不能换：必须先剥掉入站的租户头再做任何判断，否则就是给伪造开门。
app.UseAppExceptionHandling();
app.UseAppRequestLogging();
app.UseMiddleware<GatewaySecurityMiddleware>();

// 🔴 为什么必须拆成两个探针：
//
// 启动脚本是**串行**拉服务的，网关排在权限中心前面。刚起来的头几秒里权限中心
// 必然还没监听 —— 如果 /health 就去探它，脚本会判定「网关起不来」而把一个
// 完全正常的网关误杀。反过来，如果 /health 只探自己，它又会在权限中心挂掉时
// 继续回 Healthy，编排系统照样往一个鉴权全挂的网关上打流量。
//
// 于是拆开：/health 只管活着（永远快、永远不误杀），/ready 管能不能干活。
// 这也是 K8s 里 livenessProbe 与 readinessProbe 分开的同一个道理。
var probeResponse = static async (HttpContext context, HealthReport report) =>
{
    context.Response.ContentType = "application/json; charset=utf-8";

    var payload = new
    {
        status = report.Status.ToString(),
        totalMs = report.TotalDuration.TotalMilliseconds,
        // 依赖明细逐条摊平：{"PermissionService":"Healthy: 3ms","Routes":"Healthy: 33 条路由已加载"}
        dependencies = report.Entries.ToDictionary(
            a => a.Key,
            a => a.Value.Data.Count > 0
                ? string.Join("；", a.Value.Data.Select(d => $"{d.Key}={d.Value}"))
                : a.Value.Status.ToString()),
    };

    await context.Response.WriteAsJsonAsync(payload);
};

// 存活探针：只挂 self，不含任何依赖检查。慢启动或下游故障都**不该**让它变红 ——
// 把它判死等于因为别人挂了而自杀重启。
app.MapHealthChecks(
    "/health",
    new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = _ => false,
        ResponseWriter = probeResponse,
    });

// 就绪探针：探真实依赖（权限中心 + 路由表），供编排系统决定要不要摘流量。
app.MapHealthChecks(
    "/ready",
    new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        ResponseWriter = probeResponse,
    });

// Ocelot 是**终结性**中间件：匹配不到路由就直接返回 404，不会往下传给端点。
// 所以 /health 必须绕过它，否则健康检查会被 Ocelot 判成「没有对应路由」。
// 探活与业务路由在这里分开：探活给编排系统看，业务才进网关链路。
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/health")
             && !context.Request.Path.StartsWithSegments("/ready"),
    branch => branch.UseOcelot().Wait());

app.Run();
