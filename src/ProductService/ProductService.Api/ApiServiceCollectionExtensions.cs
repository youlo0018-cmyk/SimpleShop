using Collaboration.Domain.MediatR;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
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
    public static IServiceCollection AddAppServices(this IServiceCollection services)
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

        services.AddInfrastructure();
        return services;
    }
}