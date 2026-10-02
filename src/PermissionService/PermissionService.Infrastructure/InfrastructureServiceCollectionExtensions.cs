using Microsoft.Extensions.DependencyInjection;
using PermissionService.Domain.IRepository;
using PermissionService.Infrastructure.Repository;

namespace PermissionService.Infrastructure;

/// <summary>PermissionService 的基础设施注册入口。必须在 Program.cs 的 Build() 之前调用。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册仓储。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IPermissionRepository, PermissionRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        return services;
    }
}

