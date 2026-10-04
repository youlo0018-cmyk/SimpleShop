using Collaboration.Domain.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Collaboration.Domain.Messaging;

/// <summary>事件总线的 DI 注册扩展。</summary>
public static class EventBusRegistration
{
    /// <summary>注册 <see cref="IEventPublisher"/>（单例）。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    /// <returns>原集合，便于链式调用。</returns>
    /// <remarks>
    /// 必须是<b>单例</b>：RabbitMQ 的连接是重量级对象，每个请求新建一个会把 broker 打垮，
    /// 而且 TCP 握手成本远大于「发一条消息」本身。
    ///
    /// <para>配置缺失时<b>不报错</b>：事件发布是旁路，少了它主流程仍应跑得通。
    /// 这时候塞一个 Null 实现，所有发布静默丢弃并留下明确日志。</para>
    /// </remarks>
    public static IServiceCollection AddEventBus(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>();

        if (options is null || string.IsNullOrWhiteSpace(options.Host))
        {
            services.AddSingleton<IEventPublisher, NullEventPublisher>();
            return services;
        }

        services.AddSingleton(options);
        services.AddSingleton<IEventPublisher, EventBus>();
        return services;
    }
}

/// <summary>未配置 RabbitMQ 时的空实现：发布一律丢弃。</summary>
/// <remarks>
/// 存在的意义是<b>让「没配 MQ」变成可观测状态而不是运行时报错</b>。
/// 没有它的话，每个用到 <see cref="IEventPublisher"/> 的服务都得自己判空，
/// 漏一处就是 NullReferenceException。
/// </remarks>
public sealed class NullEventPublisher : IEventPublisher
{
    /// <inheritdoc />
    public Task<bool> PublishAsync<T>(string eventType, T payload, CancellationToken ct = default)
    {
        System.Diagnostics.Debug.WriteLine($"[EventBus] 未配置 RabbitMQ，事件 {eventType} 已丢弃");
        return Task.FromResult(false);
    }
}
