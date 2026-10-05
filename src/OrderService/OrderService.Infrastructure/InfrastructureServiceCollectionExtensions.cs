using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderService.Domain.Ports;
using OrderService.Infrastructure.Crypto;
using OrderService.Infrastructure.Locking;
using OrderService.Infrastructure.Ports;
using OrderService.Infrastructure.Repository;

namespace OrderService.Infrastructure;

/// <summary>OrderService 的基础设施注册入口。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>单个下游调用的超时秒数。</summary>
    /// <remarks>
    /// 一次下单要串行调营销 → 积分 → 库存（且库存是逐个 SKU），
    /// 三个 SKU 就是五次往返。设成 5 秒会在商品行多的时候随机超时，
    /// 而超时的请求可能已经把资源占上了，只能靠回滚兜底——与其冒险不如等够。
    /// </remarks>
    private const int DownstreamTimeoutSeconds = 10;

    /// <summary>注册落单端口、下游 HTTP 端口与客户锁。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置，用于读三个下游服务地址。</param>
    /// <returns>原集合，便于链式调用。</returns>
    /// <remarks>
    /// 三个下游地址缺一不可，所以在这里就 fail-fast：
    /// 启动时抛一条说清「缺哪个配置」的错误，比等到用户下单才 500 好得多。
    /// </remarks>
    public static IServiceCollection AddOrderInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IOrderStore, OrderStore>();
        services.AddScoped<IOrderCreateLock, RedisOrderCreateLock>();

        // 单例：RSA 实例本身线程安全（内部加锁），而且每次解密都涉及一次
        // 非对称运算，重新 new 一把的收益为零、开销却实打实。
        services.AddSingleton<IPickupCodeCodec>(sp => RsaPickupCodeCodec.Create(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<RsaPickupCodeCodec>>()));

        AddDownstream<ICouponPort, HttpCouponPort>(
            services, configuration, "Services:MarketingServiceBaseUrl", "营销服务");
        AddDownstream<IPointPort, HttpPointPort>(
            services, configuration, "Services:PointServiceBaseUrl", "积分服务");
        AddDownstream<IInventoryPort, HttpInventoryPort>(
            services, configuration, "Services:InventoryServiceBaseUrl", "库存服务");

        // 工作台报表的两个附属指标：数据分别在支付服务与库存服务。
        // 复用同一个 AddDownstream，超时与 fail-fast 口径与下单链路一致。
        AddDownstream<IRefundStatsPort, HttpRefundStatsPort>(
            services, configuration, "Services:PaymentServiceBaseUrl", "支付服务");
        AddDownstream<ILowStockPort, HttpLowStockPort>(
            services, configuration, "Services:InventoryServiceBaseUrl", "库存服务");

        // 发货时要往订单写物流公司名快照，字典在商品服务里。
        // 与上面几个端口一样 fail-fast：少一个地址，运营点「发货」就是 500，
        // 而错误信息只会指向订单服务，看不出根因在配置。
        AddDownstream<ILogisticsCompanyPort, HttpLogisticsCompanyPort>(
            services, configuration, "Services:ProductServiceBaseUrl", "商品服务");

        return services;
    }

    /// <summary>注册一个指向下游服务的 HttpClient 端口。</summary>
    /// <typeparam name="TPort">端口接口。</typeparam>
    /// <typeparam name="TImpl">HTTP 实现。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    /// <param name="configKey">下游地址的配置键。</param>
    /// <param name="serviceName">服务中文名，只用于拼错误信息。</param>
    /// <exception cref="InvalidOperationException">配置缺失时抛出。</exception>
    private static void AddDownstream<TPort, TImpl>(
        IServiceCollection services, IConfiguration configuration, string configKey, string serviceName)
        where TPort : class
        where TImpl : class, TPort
    {
        var baseUrl = configuration[configKey];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                $"缺少配置 {configKey}。下单要依次调用{serviceName}等下游服务，缺任何一个都无法下单。");
        }

        var address = new Uri(baseUrl.TrimEnd('/') + "/");
        var timeout = TimeSpan.FromSeconds(DownstreamTimeoutSeconds);

        services.AddHttpClient<TPort, TImpl>(client =>
        {
            client.BaseAddress = address;
            client.Timeout = timeout;
        });
    }
}
