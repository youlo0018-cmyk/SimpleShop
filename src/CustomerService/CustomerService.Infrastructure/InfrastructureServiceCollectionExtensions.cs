using CustomerService.Application.Services;
using CustomerService.Domain.IRepository;
using CustomerService.Infrastructure.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace CustomerService.Infrastructure;

/// <summary>CustomerService 的基础设施注册入口。</summary>
/// <remarks>必须在 Program.cs 的 Build() 之前调用（CODING_STANDARD 4）。</remarks>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册仓储与服务。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<ICustomerAddressRepository, CustomerAddressRepository>();
        services.AddScoped<ICustomerFavoriteRepository, CustomerFavoriteRepository>();
        services.AddScoped<CustomerTokenService>();

        // 积分服务在 S6 才建。此处先挂占位实现，契约已打通，
        // S6 只需把这一行换成真实 gRPC 客户端，注册处的其余内容不变。
        services.AddSingleton<IPointGrantClient, UnavailablePointGrantClient>();

        return services;
    }
}

