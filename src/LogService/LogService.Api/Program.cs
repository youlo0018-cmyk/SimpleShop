using Collaboration.Domain.Configuration;
using Collaboration.Domain.Infrastructure;
using Collaboration.Web;
using LogService.Api;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// S1~S5：先取配置，再连下游。
// LogService 与其它服务有两处不同，**都在这里显式豁免**：
//   1) 没有数据库——数据全在 Elasticsearch；
//   2) 不连 Redis——它不做锁、不做缓存、不需要雪花 Id
//      （写 ES 时由 ES 自己生成文档 Id，日志记录本来就不需要业务 Id）。
// 豁免比填一个假的连接串 / 空的 Redis 诚实：填了假值，后来排查的人
// 会以为它真的连了库或 Redis。
var environment = builder.Environment.EnvironmentName;
var loaded = await ServiceBootstrap.LoadConfigurationAsync(
    builder.Configuration, "LogService", environment,
    extraRequiredKeys: new List<string> { "Consul:ServiceName" },
    exemptBaseKeys: new List<string>
    {
        ConfigurationValidator.DatabaseConnectionKey,
        ConfigurationValidator.RedisConnectionKey
    });

builder.Configuration.AddConfiguration(
    new ConfigurationBuilder().AddInMemoryCollection(loaded).Build());

builder.Services.AddAppServices(builder.Configuration);
builder.Services.AddAppControllers();
builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());

builder.Services.AddAppEventLogging(builder.Configuration);

var app = builder.Build();

app.UseAppTenantContext();
app.UseAppExceptionHandling();
app.UseAppRequestLogging();
app.MapHealthChecks("/health");
app.MapControllers();
app.Run();
