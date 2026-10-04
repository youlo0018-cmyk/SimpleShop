namespace PaymentService.Application.Features.Payment;

/// <summary>支付相关开关配置。</summary>
/// <remarks>
/// 接口放在 Application、读取配置的实现在 Api：Application 层不引配置包，
/// 否则每个服务都得为了一个 bool 拖进 <c>Microsoft.Extensions.Configuration</c>。
/// </remarks>
public interface IPaymentOptions
{
    /// <summary>是否开启模拟支付。默认 true。</summary>
    /// <remarks>
    /// 关掉后后台按钮隐藏且**接口拒绝**（规格 5.28）。
    /// 只藏按钮不拦接口的话，改一下请求体照样能调通——那等于没关。
    /// </remarks>
    bool SimulateEnabled { get; }
}
