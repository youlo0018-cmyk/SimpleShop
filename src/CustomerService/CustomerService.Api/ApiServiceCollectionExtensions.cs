using System.Reflection;
using Collaboration.Domain.MediatR;
using CustomerService.Application.Features.Customer.Login;
using CustomerService.Application.Features.Customer.Register;
using CustomerService.Infrastructure;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CustomerService.Api;

/// <summary>CustomerService 的应用层注册入口。</summary>
/// <remarks>必须在 Program.cs 的 Build() 之前调用（CODING_STANDARD 4）。</remarks>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR、Validator 与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(assembly));
        services.AddValidatorsFromAssembly(assembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // Validator 与 Handler 分散在 Application 层，这里显式补上，确保不会漏注册
        services.AddScoped<IValidator<RegisterCommand>, RegisterValidator>();
        services.AddScoped<IValidator<LoginCommand>, LoginValidator>();

        services.AddInfrastructure();
        return services;
    }
}

