using Collaboration.Domain.Messaging;
using LogService.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogService.Infrastructure;

/// <summary>LogService 的基础设施注册入口。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册 Elasticsearch 读写与 RabbitMQ 重放。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置，用于读 Elasticsearch 地址。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddLogInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var esUrl = configuration["LogIndex:Url"];
        if (string.IsNullOrWhiteSpace(esUrl))
        {
            throw new InvalidOperationException(
                "缺少配置 LogIndex:Url。LogService 的全部数据都写 Elasticsearch，没有它这个服务没有任何作用。");
        }

        services.Configure<LogIndexOptions>(configuration.GetSection(LogIndexOptions.SectionName));
        services.Configure<DeadLetterReplayOptions>(
            configuration.GetSection(DeadLetterReplayOptions.SectionName));

        // 索引读写都要指向 ES，用同一个具名 HttpClient：
        // 名字固定成 "es" 是为了复用同一个连接池，
        // 否则每注册一个 AddHttpClient 就多一个连接池，ES 那边会看到一堆空闲连接。
        services.AddHttpClient("es", client =>
        {
            client.BaseAddress = new Uri(esUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddScoped<ILogIndexer>(sp => new ElasticsearchLogIndexer(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("es"),
            sp.GetRequiredService<IOptions<LogIndexOptions>>(),
            sp.GetRequiredService<ILogger<ElasticsearchLogIndexer>>()));

        services.AddScoped<ILogQuery>(sp => new ElasticsearchLogQuery(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("es"),
            sp.GetRequiredService<IOptions<LogIndexOptions>>(),
            sp.GetRequiredService<ILogger<ElasticsearchLogQuery>>()));

        // 死信仓储同时是 IDeadLetterNotifier，所以注册成单例：
        // 消费循环是后台单例，拿到的必须是同一个实例，否则每条死信都会新建一个连接池。
        services.AddSingleton<ElasticsearchDeadLetterRepository>();
        services.AddSingleton<IDeadLetterRepository>(
            sp => sp.GetRequiredService<ElasticsearchDeadLetterRepository>());

        services.AddSingleton<IDeadLetterNotifier>(
            sp => sp.GetRequiredService<ElasticsearchDeadLetterRepository>());

        services.AddSingleton<IDeadLetterReplayer, RabbitMqDeadLetterReplayer>();

        return services;
    }
}
