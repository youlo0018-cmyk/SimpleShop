using Collaboration.Domain.Configuration;
using Collaboration.Domain.Infrastructure;
using Collaboration.Web;
using ProductService.Api;
using ProductService.Domain.Entities;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ---------- S1~S5：配置源 → 数据库 / Redis → 雪花 workerId → FreeSql → 业务 ----------
var environment = builder.Environment.EnvironmentName;
var loaded = await ServiceBootstrap.LoadConfigurationAsync(
    builder.Configuration, "ProductService", environment, new List<string> { "Consul:ServiceName" });

builder.Configuration.AddConfiguration(
    new ConfigurationBuilder().AddInMemoryCollection(loaded).Build());

var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()!;
var redisOptions = builder.Configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>()!;
var snowflakeOptions = builder.Configuration.GetSection(SnowflakeOptions.SectionName).Get<SnowflakeOptions>()!;

var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions.ConnectionString);
var workerId = await ServiceBootstrap.AllocateWorkerIdAsync(
    redis.GetDatabase(redisOptions.Database), snowflakeOptions.WorkerIdKeyPrefix, "ProductService", snowflakeOptions.WorkerIdUpperBound);
SnowflakeId.Configure(workerId);

builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
builder.Services.AddSingleton(_ => redis.GetDatabase(redisOptions.Database));

builder.Services.AddAppFreeSql(
    databaseOptions.Default,
    typeof(Product).Assembly);

builder.Services.AddAppServices(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddHealthChecks().AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());

var app = builder.Build();

// 启动时确保商品索引存在。
// 🔴 失败**不阻止启动**：ES 挂了应该只是「搜不到」，不该让整个商品服务起不来。
// 索引不存在时搜索会自动降级为数据库浏览（见 QueryShopSearchHandler），功能降级但可用。
using (var scope = app.Services.CreateScope())
{
    var index = scope.ServiceProvider.GetService<ProductService.Application.Services.IProductSearchIndex>();
    if (index is null)
    {
        Console.WriteLine("[search] 未配置 Elasticsearch，跳过索引初始化（商品搜索将降级为数据库浏览）");
    }
    else if (!await index.EnsureIndexAsync())
    {
        Console.WriteLine("[search] 商品索引初始化失败，商品搜索将降级为数据库浏览");
    }
}

app.UseAppTenantContext();
app.UseAppExceptionHandling();
app.MapHealthChecks("/health");
app.MapControllers();
app.Run();