using System.Reflection;
using Collaboration.Domain.MediatR;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using UserService.Application.Features.User.ManageUser;
using UserService.Application.Services;
using UserService.Infrastructure;

namespace UserService.Api;

/// <summary>UserService 的应用层注册入口。</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR、校验器、管道与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        // 必须扫 Application 程序集：Handler 与 Validator 都在那里。
        // 用 Assembly.GetExecutingAssembly() 会注册不到任何 Handler。
        var appAssembly = typeof(CreateUserCommand).Assembly;

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(appAssembly));
        services.AddValidatorsFromAssembly(appAssembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // 校验器放在静态类里，AddValidatorsFromAssembly 扫不到，显式注册
        UserValidators.AddUserValidators(services);

        services.AddSingleton<IUserRoleClient, UnavailableUserRoleClient>();

        services.AddInfrastructure();
        return services;
    }
}