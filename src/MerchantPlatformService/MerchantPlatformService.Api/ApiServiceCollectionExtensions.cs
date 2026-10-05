using Collaboration.Domain.MediatR;
using FluentValidation;
using MediatR;
using MerchantPlatformService.Application.Features.Design;
using MerchantPlatformService.Application.Features.Merchant;
using MerchantPlatformService.Application.Features.Platform;
using MerchantPlatformService.Application.Features.Region;
using MerchantPlatformService.Application.Services;
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

        // 鉴权管道必须注册在**参数校验之前**（MediatR 按注册顺序执行）。
        // 反过来会让未授权请求先撞 400 校验错误 —— 既白做一次校验，
        // 又让「你没权限」变成「你参数填错了」，排查时被带偏。
        // 目前只有「新建平台」挂 ISuperAdminOnly，其余请求直接放行。
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(SuperAdminBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // 校验器写在嵌套静态类里，AddValidatorsFromAssembly 扫不到，必须显式注册
        PlatformValidators.AddPlatformValidators(services);
        MerchantValidators.AddMerchantValidators(services);
        RegionValidators.AddRegionValidators(services);
        DesignValidators.AddDesignValidators(services);

        AddProductPort(services, configuration);

        services.AddInfrastructure();
        return services;
    }

    /// <summary>注册商品端口：商户审核被拒 / 停用时要连带下架其商品。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    private static void AddProductPort(IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["Services:ProductServiceBaseUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "缺少配置 Services:ProductServiceBaseUrl。商户审核被拒要连带下架其商品，没有它会留下违规商品继续在售。");
        }

        services.AddHttpClient<IProductPort, HttpProductPort>(client =>
        {
            client.BaseAddress = new Uri(url!.TrimEnd('/') + "/");

            // 批量下架还要逐个同步搜索索引，商品多时比普通查询慢得多
            client.Timeout = TimeSpan.FromSeconds(60);
        });
    }
}
