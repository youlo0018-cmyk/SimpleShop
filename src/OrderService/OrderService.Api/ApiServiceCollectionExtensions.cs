using Collaboration.Domain.MediatR;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Application;
using OrderService.Application.Features.Internal;
using OrderService.Application.Features.OrderAdmin;
using OrderService.Application.Features.Orders;
using OrderService.Infrastructure;

namespace OrderService.Api;

/// <summary>OrderService 的应用层注册入口。</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR、校验器、管道、编排器与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(this IServiceCollection services, IConfiguration configuration)
    {
        var appAssembly = typeof(CreateOrderCommand).Assembly;

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(appAssembly));
        services.AddValidatorsFromAssembly(appAssembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        OrderValidators.AddOrderValidators(services);
        SeckillOrderValidators.AddSeckillOrderValidators(services);
        OrderAdminValidators.AddOrderAdminValidators(services);
        CloseTimeoutValidators.AddCloseTimeoutValidators(services);
        QueryOrderForEvaluateValidators.AddQueryOrderForEvaluateValidators(services);
        QueryOrderForPaymentValidators.AddQueryOrderForPaymentValidators(services);
        MarkOrderRefundedValidators.AddMarkOrderRefundedValidators(services);
        CompletePaymentValidators.AddCompletePaymentValidators(services);
    BatchOrderExistsValidators.AddBatchOrderExistsValidators(services);

        // 超时阈值是配置不是常量：不同业务等待时长不同，线上要临时调长时改配置比发版快
        services.Configure<OrderTimeoutOptions>(configuration.GetSection(OrderTimeoutOptions.SectionName));

        // 编排器与支付收尾必须注册成 Scoped：它们的端口依赖 IOrderStore 是 Scoped
        // （FreeSql 虽是单例，但仓储按请求注册是全项目约定）。注册成单例会在
        // Development 的 ValidateOnBuild 阶段直接启动失败——
        // 单例吃 Scoped 服务意味着这个单例会一直持有第一个请求的仓储，跨请求串数据。
        services.AddScoped<OrderCreator>();
        services.AddScoped<OrderPaymentCompleter>();
        services.AddScoped<OrderCompletionReward>();
        services.AddScoped<OrderCancellationService>();

        services.AddOrderInfrastructure(configuration);
        return services;
    }
}
