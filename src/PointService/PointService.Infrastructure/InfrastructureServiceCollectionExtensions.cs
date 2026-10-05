using Microsoft.Extensions.DependencyInjection;
using PointService.Domain.IRepository;
using PointService.Domain.Services;
using PointService.Infrastructure.Repository;
using PointService.Infrastructure.Services;

namespace PointService.Infrastructure;

/// <summary>PointService 的基础设施注册入口。必须在 Program.cs 的 Build() 之前调用。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册仓储与积分规则提供器。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IPointRepository, PointRepository>();

        // Singleton：规则快照带 30 秒缓存，必须跨请求复用，
        // 否则「缓存」根本不起作用，每次调用都变成一次数据库查询。
        services.AddSingleton<PointRuleProvider>();
        services.AddSingleton<IPointRuleProvider>(sp => sp.GetRequiredService<PointRuleProvider>());

        return services;
    }
}
