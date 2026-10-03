using Collaboration.Domain.Configuration;
using Collaboration.Domain.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScheduledService;
using ScheduledService.Jobs;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// ---------- 与其它服务同一条启动链：先从 AgileConfig 取配置，再连 Redis ----------
// 定时任务没有数据库，所以显式豁免 ConnectionStrings:Default。
// 填一个连不上的连接串只会让后来排查的人以为它真的连了库。
var loaded = await ServiceBootstrap.LoadConfigurationAsync(
    builder.Configuration,
    "ScheduledService",
    builder.Environment.EnvironmentName,
    extraRequiredKeys: ["Services:OrderServiceBaseUrl"],
    exemptBaseKeys: [ConfigurationValidator.DatabaseConnectionKey]);

builder.Configuration.AddConfiguration(
    new ConfigurationBuilder().AddInMemoryCollection(loaded).Build());

var redisOptions = builder.Configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>()!;
var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions.ConnectionString);

builder.Services.AddSingleton(_ => redis.GetDatabase(redisOptions.Database));

var orderUrl = builder.Configuration["Services:OrderServiceBaseUrl"]!;
builder.Services.AddHttpClient<IJob, OrderTimeoutCloseJob>(client =>
{
    client.BaseAddress = new Uri(orderUrl.TrimEnd('/') + "/");

    // 超时要小于任务的互斥锁 TTL（90 秒），否则锁过期后下一轮会与本轮重叠
    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHostedService<JobRunner>();

await builder.Build().RunAsync();

/// <summary>启动引导（供 IDE 与测试引用，避免 Program 类无可用类型）。</summary>
public partial class Program
{
}