using MerchantPlatformService.Domain.IRepository;
using MerchantPlatformService.Infrastructure.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantPlatformService.Infrastructure;

/// <summary>MerchantPlatformService 的基础设施注册入口。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册仓储。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IPlatformRepository, PlatformRepository>();
        services.AddScoped<IMerchantRepository, MerchantRepository>();
        services.AddScoped<IPlatformConfigRepository, PlatformConfigRepository>();
        return services;
    }
}
