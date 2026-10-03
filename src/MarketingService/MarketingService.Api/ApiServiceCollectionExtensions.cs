using Collaboration.Domain.MediatR;
using FluentValidation;
using MarketingService.Application.Features.Coupon;
using MarketingService.Application.Features.Promotion;
using MarketingService.Application.Features.Seckill;
using MarketingService.Application.Services;
using MarketingService.Infrastructure;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarketingService.Api;

/// <summary>MarketingService 的应用层注册入口。</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR、校验器、管道、库存端口与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置，用于读库存服务与商品服务地址。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        var appAssembly = typeof(ClaimCouponCommand).Assembly;

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(appAssembly));
        services.AddValidatorsFromAssembly(appAssembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // 嵌套静态类里的校验器 AddValidatorsFromAssembly 扫不到，显式注册
        CouponValidators.AddCouponValidators(services);
        PromotionValidators.AddPromotionValidators(services);
        SeckillValidators.AddSeckillValidators(services);

        // 私有静态方法不能用扩展方法语法（扩展方法要求方法可被外部访问），所以直接调用
        AddInventoryPort(services, configuration);
        AddProductPort(services, configuration);

        services.AddInfrastructure();
        return services;
    }

    /// <summary>注册库存端口：秒杀发布 / 结束要真的把库存划出与回补。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    private static void AddInventoryPort(IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["Services:InventoryServiceBaseUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "缺少配置 Services:InventoryServiceBaseUrl。秒杀发布与结束都要调用库存服务划出/回补库存，没有它场次无法发布。");
        }

        services.AddHttpClient<IInventoryPort, HttpInventoryPort>(client =>
        {
            client.BaseAddress = new Uri(url!.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
    }

    /// <summary>注册商品端口：加秒杀商品时要取 SKU 快照与售价。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    private static void AddProductPort(IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["Services:ProductServiceBaseUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "缺少配置 Services:ProductServiceBaseUrl。添加秒杀商品时要取商品快照与售价，没有它无法添加商品。");
        }

        services.AddHttpClient<IProductPort, HttpProductPort>(client =>
        {
            client.BaseAddress = new Uri(url!.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
    }
}