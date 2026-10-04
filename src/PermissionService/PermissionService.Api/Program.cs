using Collaboration.Domain.Configuration;
using Collaboration.Domain.Infrastructure;
using Collaboration.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PermissionService.Api;
using PermissionService.Domain.Entities;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// S0~S3：先取配置再连任何依赖。连接串本身也是配置的一部分（DATA_SPEC 1.2）
var environment = builder.Environment.EnvironmentName;
var extraKeys = new List<string> { "Consul:ServiceName" };

var loaded = await ServiceBootstrap.LoadConfigurationAsync(
    builder.Configuration, "PermissionService", environment, extraKeys);

var overlay = new ConfigurationBuilder().AddInMemoryCollection(loaded).Build();
builder.Configuration.AddConfiguration(overlay);

// S4~S5：Redis 连通后分配雪花 workerId，再初始化生成器
var redisOptions = builder.Configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>()!;
var snowflakeOptions = builder.Configuration.GetSection(SnowflakeOptions.SectionName).Get<SnowflakeOptions>()!;

var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions.ConnectionString);
Console.WriteLine($"[bootstrap] redis 已连接 db={redisOptions.Database}");

var workerId = await ServiceBootstrap.AllocateWorkerIdAsync(
    redis.GetDatabase(redisOptions.Database),
    snowflakeOptions.WorkerIdKeyPrefix,
    "PermissionService",
    snowflakeOptions.WorkerIdUpperBound);

SnowflakeId.Configure(workerId);

// S6~S12：注册中间件与业务，最后 Build + Run
builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
builder.Services.AddSingleton(_ => redis.GetDatabase(redisOptions.Database));

builder.Services.AddAppFreeSql(
    builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()!.Default,
    typeof(Permission).Assembly);

builder.Services.AddAppServices();
builder.Services.AddControllers();
builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());

builder.Services.AddAppEventLogging(builder.Configuration);

var app = builder.Build();
app.UseAppTenantContext();
app.UseAppExceptionHandling();
app.UseAppRequestLogging();
app.MapHealthChecks("/health");
app.MapControllers();
app.Run();

