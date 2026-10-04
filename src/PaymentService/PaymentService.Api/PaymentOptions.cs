using Microsoft.Extensions.Configuration;
using PaymentService.Application.Features.Payment;

namespace PaymentService.Api;

/// <summary>读 AgileConfig 的支付开关实现。</summary>
public sealed class PaymentOptions : IPaymentOptions
{
    /// <summary>构造选项。</summary>
    /// <param name="configuration">应用配置。</param>
    /// <remarks>
    /// 配置缺失或解析失败时**默认开启**：规格写的是「默认 true」。
    /// 反过来默认关闭的话，少配一个键就导致所有支付都失败——排障方向会被带到「支付服务坏了」。
    /// </remarks>
    public PaymentOptions(IConfiguration configuration)
    {
        var raw = configuration["Payment:SimulateEnabled"];
        SimulateEnabled = string.IsNullOrWhiteSpace(raw) || !bool.TryParse(raw, out var enabled) || enabled;
    }

    /// <inheritdoc />
    public bool SimulateEnabled { get; }
}
