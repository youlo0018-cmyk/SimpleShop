using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddInfrastructure(configuration);
        return services;
    }
}
