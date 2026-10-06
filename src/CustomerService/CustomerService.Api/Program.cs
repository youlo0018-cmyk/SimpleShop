using Collaboration.Domain.Configuration;
using Collaboration.Web;
using Collaboration.Domain.Infrastructure;
using CustomerService.Api;
using CustomerService.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// S0~S3：先取配置再连任何依赖。顺序不可调换——连接串本身也是配置的一部分（DATA_SPEC 1.2）
var environment = builder.Environment.EnvironmentName;
var extraKeys = new List<string> { "Jwt:Issuer", "Jwt:Audience", "Jwt:Secret", "Consul:ServiceName" };

var loaded = await ServiceBootstrap.LoadConfigurationAsync(
    builder.Configuration, "CustomerService", environment, extraKeys);

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
    "CustomerService",
    snowflakeOptions.WorkerIdUpperBound);

SnowflakeId.Configure(workerId);

// S6~S12：注册中间件与业务，最后 Build + Run
builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
builder.Services.AddSingleton(_ => redis.GetDatabase(redisOptions.Database));
builder.Services.AddOptions<JwtOptions>().Bind(builder.Configuration.GetSection(JwtOptions.SectionName));

builder.Services.AddAppFreeSql(
    builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()!.Default,
    typeof(CustomerService.Domain.Entities.Customer).Assembly);
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

