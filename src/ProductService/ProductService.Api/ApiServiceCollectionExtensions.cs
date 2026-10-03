using Collaboration.Domain.MediatR;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProductService.Application.Services;
using ProductService.Application.Features.Brand;
using ProductService.Application.Features.Category;
using ProductService.Application.Features.Product;
using ProductService.Infrastructure;

namespace ProductService.Api;

/// <summary>ProductService 的应用层注册入口。</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR、校验器、管道与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(this IServiceCollection services, IConfiguration configuration)
    {
        // 必须扫 Application 程序集：Handler 与 Validator 都在那里。
        // 用 Assembly.GetExecutingAssembly() 会注册不到任何 Handler。
        var appAssembly = typeof(QueryCategoryTreeCommand).Assembly;

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(appAssembly));
        services.AddValidatorsFromAssembly(appAssembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // 嵌套静态类里的校验器 AddValidatorsFromAssembly 扫不到，显式注册
        CategoryValidators.AddCategoryValidators(services);
        BrandValidators.AddBrandValidators(services);
        ProductValidators.AddProductValidators(services);

        // 库存服务地址只从配置来，代码里不写端口
        var inventoryUrl = configuration["Services:InventoryServiceBaseUrl"];
        if (string.IsNullOrWhiteSpace(inventoryUrl))
        {
            throw new InvalidOperationException(
                "缺少配置 Services:InventoryServiceBaseUrl。新建商品时要初始化库存，没有它商品保存一定失败。");
        }

        services.AddHttpClient<IInventoryClient, HttpInventoryClient>(client =>
        {
            client.BaseAddress = new Uri(inventoryUrl!.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        services.AddInfrastructure();
        return services;
    }
}