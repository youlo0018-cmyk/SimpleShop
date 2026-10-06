using Collaboration.Domain.MediatR;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentService.Application.Features.Payment;
using PaymentService.Application.Features.Refund;
using PaymentService.Application.Features.Admin;
using PaymentService.Application.Services;
using PaymentService.Infrastructure;

namespace PaymentService.Api;

/// <summary>PaymentService 的应用层注册入口。</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>注册 MediatR、校验器、管道、订单端口与基础设施。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        var appAssembly = typeof(PaymentValidators).Assembly;

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(appAssembly));
        services.AddValidatorsFromAssembly(appAssembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // 校验器写在嵌套静态类里，AddValidatorsFromAssembly 扫不到，必须显式注册
        PaymentValidators.AddPaymentValidators(services);
        RefundValidators.AddRefundValidators(services);
        PaymentAdminValidators.AddPaymentAdminValidators(services);

        services.AddSingleton<IPaymentOptions, PaymentOptions>();
        AddOrderPort(services, configuration);
        AddPlatformNameClient(services, configuration);

        services.AddInfrastructure();
        return services;
    }

    /// <summary>注册平台 / 商户名称客户端：退款详情要显示名称而不是雪花 Id（DATA_SPEC 4.3）。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    private static void AddPlatformNameClient(IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["Services:MerchantPlatformServiceBaseUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "缺少配置 Services:MerchantPlatformServiceBaseUrl，退款详情无法把平台 / 商户 Id 换成名称。");
        }

        services.AddHttpClient<IPlatformNameClient, HttpPlatformNameClient>(client =>
        {
            client.BaseAddress = new Uri(url!.TrimEnd('/') + "/");
            // 名称只是展示字段：超时给短一点，取不到就回落显示 Id，不拖慢详情
            client.Timeout = TimeSpan.FromSeconds(5);
        });
    }

    /// <summary>注册订单端口：支付与退款都要按订单的权威数据判断。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    private static void AddOrderPort(IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["Services:OrderServiceBaseUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "缺少配置 Services:OrderServiceBaseUrl。金额必须由订单服务反查，没有它无法收款也无法退款。");
        }

        services.AddHttpClient<IOrderPort, HttpOrderPort>(client =>
        {
            client.BaseAddress = new Uri(url!.TrimEnd('/') + "/");
            // 支付收尾会触发库存确认 / 积分实扣 / 券核销，比普通查询慢
            client.Timeout = TimeSpan.FromSeconds(30);
        });
    }
}
