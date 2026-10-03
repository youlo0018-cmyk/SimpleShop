using EvaluateService.Domain.IRepository;
using EvaluateService.Infrastructure.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace EvaluateService.Infrastructure;

/// <summary>EvaluateService 的基础设施注册入口。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册仓储。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IEvaluateRepository, EvaluateRepository>();
        return services;
    }
}
