using Collaboration.Domain.MediatR;
using EvaluateService.Application.Features.Evaluate;
using EvaluateService.Application.Services;
using EvaluateService.Infrastructure;
using EvaluateService.Infrastructure.Services;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EvaluateService.Api;

/// <summary>EvaluateService 的应用层注册入口。</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR、校验器、管道、端口与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置，用于读订单服务与商品服务地址。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        var appAssembly = typeof(EvaluateValidators).Assembly;

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(appAssembly));
        services.AddValidatorsFromAssembly(appAssembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // 校验器写在嵌套静态类里，AddValidatorsFromAssembly 扫不到，必须显式注册。
        // 漏了这一行 = 完全没有校验：游客能发表评价、星级能传 99、图片能传 50 张。
        EvaluateValidators.AddEvaluateValidators(services);

        AddOrderPort(services, configuration);
        AddProductPort(services, configuration);
        AddPointPort(services, configuration);

        services.AddInfrastructure();
        return services;
    }

    /// <summary>注册订单端口：发表评价前必须确认这单已完成、且确实是本人买的。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    private static void AddOrderPort(IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["Services:OrderServiceBaseUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "缺少配置 Services:OrderServiceBaseUrl。评价必须建立在真实完成的订单上，没有它无法发表评价。");
        }

        services.AddHttpClient<IOrderPort, HttpOrderPort>(client =>
        {
            client.BaseAddress = new Uri(url!.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
    }

    /// <summary>注册商品端口：每日把算好的均分回写到商品表。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    private static void AddProductPort(IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["Services:ProductServiceBaseUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "缺少配置 Services:ProductServiceBaseUrl。商品详情页要读冗余评分，没有它回写评分会失败。");
        }

        // 超时给得宽一些：每日重算会一次回写全部商品，条目多时本来就慢，
        // 超时一收紧就是「明明算对了却没同步」，而用户看到的还是旧分数
        services.AddHttpClient<IProductPort, HttpProductPort>(client =>
        {
            client.BaseAddress = new Uri(url!.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(60);
        });
    }

    /// <summary>注册积分端口：发表首评要赠送 20 积分（BUSINESS.md 13.2）。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    /// <remarks>
    /// fail-fast：缺地址时宁可启动就报错。这条途径此前**完全没有实现**
    /// （<c>evaluate.created</c> 只在 EventTopics 里声明过，没人发布），
    /// 而「静默不发积分」要到用户投诉才会被发现，且看起来像是积分服务的锅。
    /// </remarks>
    private static void AddPointPort(IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["Services:PointServiceBaseUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "缺少配置 Services:PointServiceBaseUrl。发表首评要赠送 20 积分（BUSINESS.md 13.2），"
                + "没有地址就发不出去，且失败是静默的。");
        }

        services.AddHttpClient<IPointGrantClient, HttpPointGrantClient>(client =>
        {
            client.BaseAddress = new Uri(url!.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
    }
}
