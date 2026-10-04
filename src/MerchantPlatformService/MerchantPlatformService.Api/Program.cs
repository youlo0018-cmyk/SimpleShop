using Collaboration.Domain.Configuration;
using Collaboration.Domain.Infrastructure;
using Collaboration.Web;
using MerchantPlatformService.Api;
using MerchantPlatformService.Domain.Entities;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// S1~S5：先取配置，再连数据库 / Redis、分配雪花 workerId（DATA_SPEC 1.2）
var environment = builder.Environment.EnvironmentName;
var loaded = await ServiceBootstrap.LoadConfigurationAsync(
    builder.Configuration, "MerchantPlatformService", environment, new List<string> { "Consul:ServiceName" });

builder.Configuration.AddConfiguration(
    new ConfigurationBuilder().AddInMemoryCollection(loaded).Build());

var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()!;
var redisOptions = builder.Configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>()!;
var snowflakeOptions = builder.Configuration.GetSection(SnowflakeOptions.SectionName).Get<SnowflakeOptions>()!;

var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions.ConnectionString);
Console.WriteLine($"[bootstrap] redis 已连接 db={redisOptions.Database}");

var workerId = await ServiceBootstrap.AllocateWorkerIdAsync(
    redis.GetDatabase(redisOptions.Database),
    snowflakeOptions.WorkerIdKeyPrefix, "MerchantPlatformService", snowflakeOptions.WorkerIdUpperBound);
SnowflakeId.Configure(workerId);

builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
builder.Services.AddSingleton(_ => redis.GetDatabase(redisOptions.Database));

builder.Services.AddAppFreeSql(
    databaseOptions.Default,
    typeof(Platform).Assembly);

builder.Services.AddAppServices(builder.Configuration);
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
