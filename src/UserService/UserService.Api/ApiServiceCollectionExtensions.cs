using System.Reflection;
using Collaboration.Domain.Configuration;
using Collaboration.Domain.MediatR;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using UserService.Application.Features.Internal;
using UserService.Application.Features.User.ManageUser;
using UserService.Application.Services;
using UserService.Infrastructure;

namespace UserService.Api;

/// <summary>UserService 的应用层注册入口。</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR、校验器、管道与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置，用于读下游服务地址。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(this IServiceCollection services, IConfiguration configuration)
    {
        // 必须扫 Application 程序集：Handler 与 Validator 都在那里。
        // 用 Assembly.GetExecutingAssembly() 会注册不到任何 Handler。
        var appAssembly = typeof(CreateUserCommand).Assembly;

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(appAssembly));
        services.AddValidatorsFromAssembly(appAssembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // 校验器放在静态类里，AddValidatorsFromAssembly 扫不到，显式注册
        UserValidators.AddUserValidators(services);
        AuthenticateAdminValidators.AddAuthenticateAdminValidators(services);

        // 角色绑定走内网 HTTP 打到权限中心。地址只在配置里出现，不写死。
        var permissionServiceUrl = configuration["Services:PermissionServiceBaseUrl"];
        if (string.IsNullOrWhiteSpace(permissionServiceUrl))
        {
            throw new InvalidOperationException(
                "缺少配置 Services:PermissionServiceBaseUrl，建号时无法绑定角色。请在 AgileConfig 补上。");
        }

        services.AddHttpClient<IUserRoleClient, HttpUserRoleClient>(client =>
        {
            client.BaseAddress = new Uri(permissionServiceUrl!.TrimEnd('/') + "/");
            // 权限中心不可用时要尽快失败，不能让建号请求一直挂着
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        // 会话吊销键写在共享库：读它的是网关，写错库号会静默失效（DATA_SPEC 5.20）。
        var sharedDatabase = configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>()?.SharedDatabase ?? 0;
        services.AddSingleton<IAdminSessionRevoker>(sp => new RedisAdminSessionRevoker(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sharedDatabase,
            sp.GetRequiredService<ILogger<RedisAdminSessionRevoker>>()));

        services.AddInfrastructure();
        return services;
    }
}
