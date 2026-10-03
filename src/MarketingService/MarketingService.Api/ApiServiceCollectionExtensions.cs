using Collaboration.Domain.MediatR;
using FluentValidation;
using MarketingService.Application.Features.Coupon;
using MarketingService.Application.Features.Promotion;
using MarketingService.Infrastructure;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace MarketingService.Api;

/// <summary>MarketingService 的应用层注册入口。</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR、校验器、管道与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        var appAssembly = typeof(ClaimCouponCommand).Assembly;

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(appAssembly));
        services.AddValidatorsFromAssembly(appAssembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        CouponValidators.AddCouponValidators(services);
        PromotionValidators.AddPromotionValidators(services);

        services.AddInfrastructure();
        return services;
    }
}