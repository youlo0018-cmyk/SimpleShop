using System.Reflection;
using Collaboration.Domain.MediatR;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using PermissionService.Application.Features.Permission.QueryTree;
using PermissionService.Infrastructure;
using PermissionService.Application.Features.Role;
using PermissionService.Application.Features.Internal;

namespace PermissionService.Api;

/// <summary>PermissionService 的应用层注册入口。</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR、校验器、管道与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        // 必须扫 Application 程序集：Handler 与 Validator 都在那里。
        // 用 Assembly.GetExecutingAssembly() 会注册不到任何 Handler，
        // 运行时报 "No service for type IRequestHandler<...> has been registered"。
        var appAssembly = typeof(QueryPermissionTreeCommand).Assembly;

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(appAssembly));
        services.AddValidatorsFromAssembly(appAssembly);
        // 管道顺序有意义：鉴权必须在参数校验之前。
        // 否则未授权请求会先撞到 400 校验错误，白做一次参数校验，语义也不对。
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(SuperAdminBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // AddValidatorsFromAssembly 扫不到嵌套静态类里的校验器，这里显式注册
        RoleValidators.AddRoleValidators(services);
        RoleOptionValidators.AddRoleOptionValidators(services);
        ResolvePermissionsValidators.AddResolvePermissionsValidators(services);

        services.AddInfrastructure();
        return services;
    }
}

