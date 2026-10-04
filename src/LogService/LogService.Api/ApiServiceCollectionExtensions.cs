using Collaboration.Domain.Configuration;
using Collaboration.Domain.MediatR;
using Collaboration.Domain.Messaging;
using Collaboration.Web.Messaging;
using FluentValidation;
using LogService.Application;
using LogService.Infrastructure;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LogService.Api;

/// <summary>LogService 的应用层注册入口。</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR、校验器、管道、事件消费与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        var appAssembly = typeof(LogValidators).Assembly;

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(appAssembly));
        services.AddValidatorsFromAssembly(appAssembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // 校验器在嵌套静态类里，AddValidatorsFromAssembly 扫不到，必须显式注册
        LogValidators.AddLogValidators(services);

        services.AddLogInfrastructure(configuration);

        AddEventConsumer(services, configuration);

        return services;
    }

    /// <summary>注册三个事件处理器与消费循环。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    /// <remarks>
    /// 处理器注册成 <b>Scoped</b> 而不是 Singleton：处理器依赖 <c>ILogIndexer</c>，
    /// 而它包着一个 HttpClient。用 Singleton 会让消费循环这个后台单例
    /// 一直持有某个 scope 的东西——虽然 HttpClient 本身线程安全，
    /// 但一旦哪天处理器里加了带 scope 的依赖（比如 DbContext），就会变成 captive dependency。
    /// 消费循环本身是单例，它<b>只持有 handler 的工厂</b>，
    /// 每条消息进来时开一个 scope，处理完就释放。
    /// </remarks>
    private static void AddEventConsumer(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IEventHandler, PvLogHandler>();
        services.AddScoped<IEventHandler, OperationLogHandler>();
        services.AddScoped<IEventHandler, ExceptionLogHandler>();

        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.Configure<EventConsumerOptions>(
            configuration.GetSection(EventConsumerOptions.SectionName));

        // 消费循环是后台服务，用单例。它自己开作用域取 handler，
        // 所以处理器必须注册成 Scoped 才不会被钉死在根作用域里。
        services.AddHostedService<EventConsumerService>();
    }
}
