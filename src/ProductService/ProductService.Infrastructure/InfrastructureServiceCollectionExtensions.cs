using Microsoft.Extensions.DependencyInjection;
using ProductService.Domain.IRepository;
using ProductService.Infrastructure.Repository;

namespace ProductService.Infrastructure;

/// <summary>ProductService 的基础设施注册入口。必须在 Program.cs 的 Build() 之前调用。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册仓储。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原集合。</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        return services;
    }
}