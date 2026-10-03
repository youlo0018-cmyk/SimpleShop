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
    extraRequiredKeys: ["Services:OrderServiceBaseUrl", "Services:PointServiceBaseUrl"],
    exemptBaseKeys: [ConfigurationValidator.DatabaseConnectionKey]);

builder.Configuration.AddConfiguration(
    new ConfigurationBuilder().AddInMemoryCollection(loaded).Build());

var redisOptions = builder.Configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>()!;
var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions.ConnectionString);

builder.Services.AddSingleton(_ => redis.GetDatabase(redisOptions.Database));

var orderUrl = builder.Configuration["Services:OrderServiceBaseUrl"]!;
var pointUrl = builder.Configuration["Services:PointServiceBaseUrl"]!;
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

builder.Services.AddHostedService<JobRunner>();

await builder.Build().RunAsync();

/// <summary>启动引导（供 IDE 与测试引用，避免 Program 类无可用类型）。</summary>
public partial class Program
{
}