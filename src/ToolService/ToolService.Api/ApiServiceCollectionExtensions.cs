using Collaboration.Domain.MediatR;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToolService.Application.Features.Manage;
using ToolService.Application.Features.Upload;
using ToolService.Infrastructure;
using ToolService.Infrastructure.Repository;

namespace ToolService.Api;

/// <summary>ToolService 的应用层注册入口。</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR 与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Handler 在 Application 程序集，必须扫它而不是当前程序集
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(UploadFileCommand).Assembly));

        // 🔴 之前**漏了**校验管道：只有 AddMediatR 没有 AddValidatorsFromAssembly 与
        // ValidationBehavior，于是本服务的命令**一条校验都不会跑**。
        // 症状是「参数完全不合法也照样执行」，而且没有任何报错——
        // 文件列表按 pageSize=999999 查、或按 category=xxx 查都会静默返回空列表，
        // 用户只会以为「没有文件」。其余 15 个服务都注册了这两行，只有这里漏了。
        services.AddValidatorsFromAssembly(typeof(UploadFileCommand).Assembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // 嵌套静态类里的校验器 AddValidatorsFromAssembly 扫不到，显式注册
        ManageFileValidators.AddManageFileValidators(services);

        services.AddInfrastructure(configuration);
        return services;
    }
}
