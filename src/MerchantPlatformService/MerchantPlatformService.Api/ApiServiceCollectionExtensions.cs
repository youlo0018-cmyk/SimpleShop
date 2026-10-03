using Collaboration.Domain.MediatR;
using FluentValidation;
using MediatR;
using MerchantPlatformService.Application.Features.Platform;
using MerchantPlatformService.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantPlatformService.Api;

/// <summary>MerchantPlatformService 的应用层注册入口。</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR、校验器、管道与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        var appAssembly = typeof(PlatformValidators).Assembly;

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(appAssembly));
        services.AddValidatorsFromAssembly(appAssembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // 校验器写在嵌套静态类里，AddValidatorsFromAssembly 扫不到，必须显式注册
        PlatformValidators.AddPlatformValidators(services);

        services.AddInfrastructure();
        return services;
    }
}
