using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProductService.Application.Services;
using ProductService.Domain.IRepository;
using ProductService.Infrastructure.Repository;
using ProductService.Infrastructure.Search;

namespace ProductService.Infrastructure;

/// <summary>ProductService 的基础设施注册入口。必须在 Program.cs 的 Build() 之前调用。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册仓储与商品搜索索引。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置，用于读 Elasticsearch 地址。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IBrandRepository, BrandRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();

        AddSearchIndex(services, configuration);
        return services;
    }

    /// <summary>注册 Elasticsearch 商品索引。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    private static void AddSearchIndex(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(ProductSearchOptions.SectionName);
        var options = section.Get<ProductSearchOptions>() ?? new ProductSearchOptions();

        if (string.IsNullOrWhiteSpace(options.Url))
        {
            // 配置缺失时不抛异常：没有搜索只是「搜不到」，不该让整个商品服务起不来。
            // 启动时记一条 Error 日志，比「服务直接挂掉」对排障友好得多
            Console.WriteLine("[search] 未配置 Elasticsearch:Url，商品搜索将返回空结果（不影响其它功能）");
            return;
        }

        services.Configure<ProductSearchOptions>(section);

        // 用 typed client 即可：HttpClientFactory 已经处理了连接池与 DNS 刷新，
        // 再额外注册一个单例只会多出一条注册项，让人以为要自己管生命周期。
        services.AddHttpClient<IProductSearchIndex, ElasticsearchProductSearchIndex>(client =>
        {
            client.BaseAddress = new Uri(options.Url.TrimEnd('/') + "/");

            // 搜索是前台列表的同步调用，不能让它拖慢页面；ES 抖动时宁可返回空结果
            client.Timeout = TimeSpan.FromSeconds(5);
        });
    }
}