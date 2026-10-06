using Collaboration.Domain.Configuration;
using Collaboration.Domain.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScheduledService;
using ScheduledService.Jobs;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// 孤儿对账要用两个不同 BaseAddress 的 HttpClient（见下方注册处）
const string OrphanInventoryClient = "orphan_inventory";
const string OrphanOrderClient = "orphan_order";
const string ActivityCleanupMarketingClient = "activity_cleanup_marketing";
const string ActivityCleanupOrderClient = "activity_cleanup_order";

// ---------- 与其它服务同一条启动链：先从 AgileConfig 取配置，再连 Redis ----------
// 定时任务没有数据库，所以显式豁免 ConnectionStrings:Default。
// 填一个连不上的连接串只会让后来排查的人以为它真的连了库。
var loaded = await ServiceBootstrap.LoadConfigurationAsync(
    builder.Configuration,
    "ScheduledService",
    builder.Environment.EnvironmentName,
    extraRequiredKeys:
    [
        "Services:OrderServiceBaseUrl",
        "Services:PointServiceBaseUrl",
        "Services:EvaluateServiceBaseUrl",
        "Services:InventoryServiceBaseUrl",
        "Services:MarketingServiceBaseUrl"
    ],
    exemptBaseKeys: [ConfigurationValidator.DatabaseConnectionKey]);

builder.Configuration.AddConfiguration(
    new ConfigurationBuilder().AddInMemoryCollection(loaded).Build());

var redisOptions = builder.Configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>()!;
var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions.ConnectionString);

builder.Services.AddSingleton(_ => redis.GetDatabase(redisOptions.Database));

var orderUrl = builder.Configuration["Services:OrderServiceBaseUrl"]!;
var pointUrl = builder.Configuration["Services:PointServiceBaseUrl"]!;
var productUrl = builder.Configuration["Services:ProductServiceBaseUrl"]!;
var evaluateUrl = builder.Configuration["Services:EvaluateServiceBaseUrl"]!;
var inventoryUrl = builder.Configuration["Services:InventoryServiceBaseUrl"]!;
var marketingUrl = builder.Configuration["Services:MarketingServiceBaseUrl"]!;
builder.Services.AddHttpClient<OrderTimeoutCloseJob>(client =>
{
    client.BaseAddress = new Uri(orderUrl.TrimEnd('/') + "/");

    // 超时要小于任务的互斥锁 TTL（90 秒），否则锁过期后下一轮会与本轮重叠
    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHttpClient<PointExpireJob>(client =>
{
    client.BaseAddress = new Uri(pointUrl.TrimEnd('/') + "/");

    // 过期要逐个批次处理，量大时比关单慢得多，给足时间
    client.Timeout = TimeSpan.FromSeconds(120);
});

// 按**具体类型**注册成 IJob，而不是 AddHttpClient<IJob, TJob>()。
// 后者会把两个任务都注册成同一个服务类型 IJob，行为依赖「同类型多注册」的实现细节；
// 写成 IJob → 具体类型的显式映射，读代码的人一眼就知道有哪几个任务、各自指向谁。
builder.Services.AddTransient<IJob>(sp => sp.GetRequiredService<OrderTimeoutCloseJob>());
builder.Services.AddTransient<IJob>(sp => sp.GetRequiredService<PointExpireJob>());
builder.Services.AddTransient<IJob>(sp => sp.GetRequiredService<ProductSearchIndexSyncJob>());
builder.Services.AddTransient<IJob>(sp => sp.GetRequiredService<EvaluateRecomputeJob>());
builder.Services.AddTransient<IJob>(sp => sp.GetRequiredService<StockReleaseCompensateJob>());
builder.Services.AddTransient<IJob>(sp => sp.GetRequiredService<OrphanLockReconcileJob>());
builder.Services.AddTransient<IJob>(sp => sp.GetRequiredService<SeckillSessionFinishJob>());

builder.Services.AddHttpClient<StockReleaseCompensateJob>(client =>
{
    client.BaseAddress = new Uri(inventoryUrl.TrimEnd('/') + "/");

    // 补偿重试会逐条做库存变更 + 写流水，量大时比普通查询慢
    client.Timeout = TimeSpan.FromSeconds(120);
});

// 孤儿对账要同时问库存服务（哪些锁是候选）和订单服务（这些单号是否真的不存在），
// 所以需要**两个 BaseAddress 不同的 HttpClient**。
// 注意不能用 AddHttpClient<T>() 注册两次——typed client 只会保留最后一次，
// 而任务构造函数要的是两个 HttpClient，DI 根本满足不了。改用命名客户端 + 工厂。
builder.Services.AddHttpClient(OrphanInventoryClient, client =>
{
    client.BaseAddress = new Uri(inventoryUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(120);
});

builder.Services.AddHttpClient(OrphanOrderClient, client =>
{
    client.BaseAddress = new Uri(orderUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(120);
});

builder.Services.AddTransient(sp => new OrphanLockReconcileJob(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(OrphanInventoryClient),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(OrphanOrderClient),
    sp.GetRequiredService<ILogger<OrphanLockReconcileJob>>()));

// 活动参与记录孤儿清理：同样要同时问营销服务（候选）与订单服务（是否存在）。
builder.Services.AddHttpClient(ActivityCleanupMarketingClient, client =>
{
    client.BaseAddress = new Uri(marketingUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHttpClient(ActivityCleanupOrderClient, client =>
{
    client.BaseAddress = new Uri(orderUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddTransient(sp => new ActivityRecordCleanupJob(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(ActivityCleanupMarketingClient),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(ActivityCleanupOrderClient),
    sp.GetRequiredService<ILogger<ActivityRecordCleanupJob>>()));

builder.Services.AddTransient<IJob>(sp => sp.GetRequiredService<ActivityRecordCleanupJob>());

builder.Services.AddHttpClient<ProductSearchIndexSyncJob>(client =>
{
    client.BaseAddress = new Uri(productUrl.TrimEnd('/') + "/");

    // 对账要扫全表并逐个补写，比关单慢得多，给足时间
    client.Timeout = TimeSpan.FromSeconds(180);
});

builder.Services.AddHttpClient<EvaluateRecomputeJob>(client =>
{
    client.BaseAddress = new Uri(evaluateUrl.TrimEnd('/') + "/");

    // 重算要扫全量评价并逐个回写商品表，比索引对账还慢
    client.Timeout = TimeSpan.FromSeconds(300);
});

// 秒杀场次到点自动结束：没有它，正常打完的场次会永远停在「进行中」，
// 剩余库存永久锁在秒杀池里且没有任何报错（现象只是商品一直缺货）。
builder.Services.AddHttpClient<SeckillSessionFinishJob>(client =>
{
    client.BaseAddress = new Uri(marketingUrl.TrimEnd('/') + "/");

    // 单轮最多 50 个场次、逐个回补库存，比普通查询慢
    client.Timeout = TimeSpan.FromSeconds(90);
});

builder.Services.AddHostedService<JobRunner>();

await builder.Build().RunAsync();

/// <summary>启动引导（供 IDE 与测试引用，避免 Program 类无可用类型）。</summary>
public partial class Program
{
}
