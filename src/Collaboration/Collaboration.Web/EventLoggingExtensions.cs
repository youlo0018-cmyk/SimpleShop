using Collaboration.Domain.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Collaboration.Web;

/// <summary>请求 / 异常日志的事件发布注册入口，所有服务共用。</summary>
/// <remarks>
/// 单独一个方法而不是让每个 Program.cs 各写一遍：漏一处的话，
/// 那个服务要么启动时 DI 校验失败，要么（更糟）日志静默不发，
/// 而「某个服务的日志查不到」这种问题往往要过很久才被发现。
/// </remarks>
public static class EventLoggingExtensions
{
    /// <summary>注册事件发布端口，供请求 / 异常日志中间件使用。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置，用于读 RabbitMQ 连接串。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppEventLogging(
        this IServiceCollection services, IConfiguration configuration)
    {
        // AddEventBus 自身幂等（内部先查容器里有没有），所以
        // ProductService 这类「自己就发业务事件」的服务重复调用也不会
        // 注册出两个 IEventPublisher——那样 DI 取到哪个不确定，
        // 日志事件和业务事件可能各走一条连接。
        services.AddEventBus(configuration);
        return services;
    }
}
