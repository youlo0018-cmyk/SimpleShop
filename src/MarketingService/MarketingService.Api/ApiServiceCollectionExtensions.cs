using Collaboration.Domain.MediatR;
using FluentValidation;
using MarketingService.Application.Features.Coupon;
using MarketingService.Application.Features.Promotion;
using MarketingService.Application.Features.Seckill;
using MarketingService.Application.Services;
using MarketingService.Domain.Services;
using MarketingService.Infrastructure;
using MarketingService.Infrastructure.Ports;
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
        AdminCouponValidators.AddAdminCouponValidators(services);
        PromotionValidators.AddPromotionValidators(services);
        SeckillValidators.AddSeckillValidators(services);
        GrabValidators.AddGrabValidators(services);

        // 私有静态方法不能用扩展方法语法（扩展方法要求方法可被外部访问），所以直接调用
        AddInventoryPort(services, configuration);
        AddProductPort(services, configuration);
        AddOrderPort(services, configuration);
        AddSeckillGmvPort(services, configuration);

        // 库存回补器：手动中止与「到点自动结束」共用同一段回补代码。
        // 注册成 Scoped 而不是 Transient：它持有仓储与 HttpClient，
        // 一次请求内复用同一个实例即可，不需要每次新建。
        services.AddScoped<SeckillStockReturner>();

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

    /// <summary>注册订单端口：抢购要真的调订单服务建单。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    private static void AddOrderPort(IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["Services:OrderServiceBaseUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "缺少配置 Services:OrderServiceBaseUrl。抢购要调订单服务建单，没有它抢中了也落不了单。");
        }

        // 超时给得比库存/商品端口宽：秒杀高峰期订单服务可能被别的流量压住，
        // 超时一收紧就是「明明抢中了却提示下单失败」，用户会直接投诉。
        services.AddHttpClient<IOrderPort, HttpOrderPort>(client =>
        {
            client.BaseAddress = new Uri(url!.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(15);
        });
    }

    /// <summary>注册秒杀 GMV 端口：报表要向订单服务问成交额。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    private static void AddSeckillGmvPort(IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["Services:OrderServiceBaseUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "缺少配置 Services:OrderServiceBaseUrl。秒杀订单金额在订单库，没有它 GMV 算不出来。");
        }

        // 超时和下单端口一致：都是打订单服务，但这个是只读汇总，
        // 不参与下单链路，失败只影响报表里的一个数字。
        services.AddHttpClient<ISeckillGmvPort, HttpSeckillGmvPort>(client =>
        {
            client.BaseAddress = new Uri(url!.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(15);
        });
    }
}
