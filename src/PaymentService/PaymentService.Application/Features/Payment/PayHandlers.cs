using Collaboration.Domain.Common;
using MediatR;
using PaymentService.Application.Services;
using PaymentService.Domain.IRepository;

namespace PaymentService.Application.Features.Payment;

/// <summary>确认支付处理器（C 端：用户在支付页点了「确认支付」）。</summary>
public sealed class ConfirmPaymentHandler : IRequestHandler<ConfirmPaymentCommand, ApiResponse<PaymentDto>>
{
    private readonly IPaymentRepository _payments;
    private readonly IOrderPort _orders;

    /// <summary>构造处理器。</summary>
    /// <param name="payments">支付仓储。</param>
    /// <param name="orders">订单服务端口。</param>
    public ConfirmPaymentHandler(IPaymentRepository payments, IOrderPort orders)
    {
        _payments = payments;
        _orders = orders;
    }

    /// <summary>执行确认。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>支付单视图。</returns>
    public async Task<ApiResponse<PaymentDto>> Handle(ConfirmPaymentCommand request, CancellationToken ct)
        => await PaymentExecutor.ExecuteAsync(request.OrderNo, succeed: true, _payments, _orders, null, ct);
}

/// <summary>模拟支付处理器（后台订单列表每行的按钮）。</summary>
public sealed class SimulatePaymentHandler : IRequestHandler<SimulatePaymentCommand, ApiResponse<PaymentDto>>
{
    private readonly IPaymentRepository _payments;
    private readonly IOrderPort _orders;
    private readonly IPaymentOptions _options;

    /// <summary>构造处理器。</summary>
    /// <param name="payments">支付仓储。</param>
    /// <param name="orders">订单服务端口。</param>
    /// <param name="options">支付开关配置。</param>
    public SimulatePaymentHandler(IPaymentRepository payments, IOrderPort orders, IPaymentOptions options)
    {
        _payments = payments;
        _orders = orders;
        _options = options;
    }

    /// <summary>执行模拟支付。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>支付单视图。</returns>
    public async Task<ApiResponse<PaymentDto>> Handle(SimulatePaymentCommand request, CancellationToken ct)
    {
        // 受 AgileConfig Payment:SimulateEnabled 控制。只藏按钮不拦接口的话，
        // 改一下请求体照样能调通——那等于没关
        if (!_options.SimulateEnabled)
        {
            return ApiResults.Fail<PaymentDto>(BaseApiResponseCode.Forbidden, "当前环境已关闭模拟支付");
        }

        return await PaymentExecutor.ExecuteAsync(
            request.OrderNo, request.Success, _payments, _orders, null, ct);
    }
}
