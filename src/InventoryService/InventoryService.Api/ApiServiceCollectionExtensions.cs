using Collaboration.Domain.MediatR;
using FluentValidation;
using InventoryService.Application.Features.Internal;
using InventoryService.Application.Features.Operations;
using InventoryService.Infrastructure;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryService.Api;

/// <summary>InventoryService 的应用层注册入口。</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR、校验器、管道与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        var appAssembly = typeof(ApplyStockCommand).Assembly;

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(appAssembly));
        services.AddValidatorsFromAssembly(appAssembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        StockValidators.AddStockValidators(services);
    CompensateStockReleasesValidators.AddCompensateStockReleasesValidators(services);
    ReconcileOrphanLocksValidators.AddReconcileOrphanLocksValidators(services);

        services.AddInfrastructure();
        return services;
    }
}
