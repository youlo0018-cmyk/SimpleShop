using Collaboration.Domain.MediatR;
using Collaboration.Domain.Messaging;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProductService.Application.Services;
using ProductService.Application.Features.Brand;
using ProductService.Application.Features.Category;
using ProductService.Application.Features.Internal;
using ProductService.Application.Features.Product;
using ProductService.Application.Features.Shop;
using ProductService.Infrastructure;

namespace ProductService.Api;

/// <summary>ProductService 的应用层注册入口。</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR、校验器、管道与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置，用于读下游服务与 Elasticsearch 地址。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(this IServiceCollection services, IConfiguration configuration)
    {
        // 必须扫 Application 程序集：Handler 与 Validator 都在那里。
        // 用 Assembly.GetExecutingAssembly() 会注册不到任何 Handler。
        var appAssembly = typeof(QueryCategoryTreeCommand).Assembly;

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(appAssembly));
        services.AddValidatorsFromAssembly(appAssembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // 列表查询与搜索查询共用同一份到手价组装逻辑，单独注册一个 Scoped
        services.AddScoped<ShopItemAssembler>();

        // 嵌套静态类里的校验器 AddValidatorsFromAssembly 扫不到，显式注册
        CategoryValidators.AddCategoryValidators(services);
        BrandValidators.AddBrandValidators(services);
        ProductValidators.AddProductValidators(services);
        ShopValidators.AddShopValidators(services);
        SearchIndexSyncValidators.AddSearchIndexSyncValidators(services);
    SyncProductRatingsValidators.AddSyncProductRatingsValidators(services);
    OffShelfProductsByMerchantValidators.AddOffShelfProductsByMerchantValidators(services);
    CheckProductsForDesignValidators.AddCheckProductsForDesignValidators(services);

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

        // 营销服务地址只从配置来，代码里不写端口。
        // 前台商品列表与详情都要拿到手价，少了它整个前台就显示不出优惠。
        var marketingUrl = configuration["Services:MarketingServiceBaseUrl"];
        if (string.IsNullOrWhiteSpace(marketingUrl))
        {
            throw new InvalidOperationException(
                "缺少配置 Services:MarketingServiceBaseUrl。前台商品列表与详情要展示到手价，没有它前台就没有优惠信息。");
        }

        services.AddHttpClient<IShopPriceClient, HttpShopPriceClient>(client =>
        {
            client.BaseAddress = new Uri(marketingUrl!.TrimEnd('/') + "/");

            // 列表页一次要试算几十个 SKU 的到手价，给得比普通内部调用宽一些；
            // 但也别给太大：营销服务慢下来时前台会跟着卡住。
            client.Timeout = TimeSpan.FromSeconds(8);
        });

        services.AddInfrastructure(configuration);
    services.AddEventBus(configuration);
        return services;
    }
}
