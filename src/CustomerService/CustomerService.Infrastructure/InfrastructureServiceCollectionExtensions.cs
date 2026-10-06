using CustomerService.Application.Services;
using CustomerService.Domain.IRepository;
using CustomerService.Infrastructure.Repository;
using CustomerService.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CustomerService.Infrastructure;

/// <summary>CustomerService 的基础设施注册入口。</summary>
/// <remarks>必须在 Program.cs 的 Build() 之前调用（CODING_STANDARD 4）。</remarks>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册仓储与服务。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置，用于取积分服务地址。</param>
    /// <returns>原集合，便于链式调用。</returns>
    /// <exception cref="InvalidOperationException">缺少积分服务地址时抛出。</exception>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<ICustomerAddressRepository, CustomerAddressRepository>();
        services.AddScoped<ICustomerFavoriteRepository, CustomerFavoriteRepository>();
        services.AddScoped<CustomerTokenService>();

        // 注册赠送 +100 积分（BUSINESS.md 13.2）。
        //
        // 🔴 这里以前挂的是 UnavailablePointGrantClient —— 一个永远返回 false、
        // 只打一行日志的占位实现。契约「打通」了，但**积分从来没发出去过**：
        // 实测注册后 available=0、totalEarned=0，而规格要求 +100。
        // 占位实现最坏的地方是它看起来像已经接好了 —— 调用点、返回值、日志都有，
        // 只有积分是假的。
        var pointUrl = configuration["Services:PointServiceBaseUrl"];
        if (string.IsNullOrWhiteSpace(pointUrl))
        {
            // fail-fast：宁可启动时就报错，也不要「起来了但注册赠送永远是 0」——
            // 后者要到用户投诉才发现，而且看起来像是积分服务的锅。
            throw new InvalidOperationException(
                "缺少配置 Services:PointServiceBaseUrl。注册要赠送 100 积分（BUSINESS.md 13.2），"
                + "没有地址就发不出去，且失败是静默的。");
        }

        services.AddHttpClient<IPointGrantClient, HttpPointGrantClient>(client =>
        {
            client.BaseAddress = new Uri(pointUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        return services;
    }
}

