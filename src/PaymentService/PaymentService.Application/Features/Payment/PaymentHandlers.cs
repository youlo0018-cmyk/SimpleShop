using Collaboration.Domain.Common;
using MediatR;
using PaymentService.Application.Services;
using PaymentService.Domain.Entities;
using PaymentService.Domain.IRepository;
using PaymentService.Domain.Services;
using Microsoft.Extensions.Logging;

namespace PaymentService.Application.Features.Payment;

/// <summary>创建支付单处理器。</summary>
public sealed class CreatePaymentHandler : IRequestHandler<CreatePaymentCommand, ApiResponse<PaymentDto>>
{
    private readonly IPaymentRepository _payments;
    private readonly IOrderPort _orders;

    /// <summary>构造处理器。</summary>
    /// <param name="payments">支付仓储。</param>
    /// <param name="orders">订单服务端口。</param>
    public CreatePaymentHandler(IPaymentRepository payments, IOrderPort orders)
    {
        _payments = payments;
        _orders = orders;
    }

    /// <summary>执行创建。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>支付单视图。</returns>
    public async Task<ApiResponse<PaymentDto>> Handle(CreatePaymentCommand request, CancellationToken ct)
    {
        var orderNo = request.OrderNo.Trim();

        var order = await _orders.GetForPaymentAsync(orderNo, ct).ConfigureAwait(false);
        if (order is null)
        {
            return ApiResults.Fail<PaymentDto>(BaseApiResponseCode.NotFound, "订单不存在或订单服务不可用");
        }

        // C 端入口：只能给自己的订单发起支付（订单号是可枚举的字符串，不校验就是越权）
        var notOwner = PaymentOwnership.RejectIfNotOwner(order);
        if (notOwner is not null) return notOwner;

        if (order.Status != OrderStatusNumbers.PendingPayment)
        {
            return ApiResults.Fail<PaymentDto>(
                BaseApiResponseCode.OrderStateInvalid,
                $"当前订单状态是「{order.StatusName}」，只有待支付的订单可以发起支付");
        }

        var payment = new PaymentOrder
        {
            PaymentNo = BuildPaymentNo(),
            OrderId = order.OrderId,
            OrderNo = order.OrderNo,
            // 🔴 金额来自订单服务反查，**不接受客户端传入**（规格 10.1）
            Amount = RefundRules.Round2(order.PayableAmount),
            Status = PaymentStatuses.Pending,
            Channel = PaymentChannels.Simulate,
            PlatformId = order.PlatformId,
            MerchantId = order.MerchantId
        };

        // 幂等键 {order_no}:{channel}：用户反复点「去支付」拿到同一张单，而不是造一堆单
        await _payments.InsertOrGetAsync(payment, ct).ConfigureAwait(false);

        var stored = await _payments.GetByOrderNoAsync(orderNo, ct).ConfigureAwait(false);
        return ApiResults.Ok(PaymentAssembler.Build(stored!, order.StatusName), "支付单已创建");
    }

    /// <summary>生成支付单号：时间戳 + 6 位随机数。</summary>
    /// <returns>支付单号。</returns>
    private static string BuildPaymentNo()
        => $"PAY{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.Next(100000, 1000000)}";
}

/// <summary>查支付单处理器。</summary>
public sealed class QueryPaymentHandler : IRequestHandler<QueryPaymentCommand, ApiResponse<PaymentDto>>
{
    private readonly IPaymentRepository _payments;
    private readonly IOrderPort _orders;

    /// <summary>构造处理器。</summary>
    /// <param name="payments">支付仓储。</param>
    /// <param name="orders">订单端口，只用于校验归属（防 IDOR）。</param>
    public QueryPaymentHandler(IPaymentRepository payments, IOrderPort orders)
    {
        _payments = payments;
        _orders = orders;
    }

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>支付单视图。</returns>
    /// <remarks>
    /// 查支付单也要验归属：它带着金额与支付状态，是别人订单的信息。
    /// 代价是每次查询多一次内网调用 —— 换来的是「顾客看不到别人的单」。
    /// </remarks>
    public async Task<ApiResponse<PaymentDto>> Handle(QueryPaymentCommand request, CancellationToken ct)
    {
        var (_, failure) = await PaymentOwnership.LoadOwnedAsync(_orders, request.OrderNo, ct)
            .ConfigureAwait(false);
        if (failure is not null) return failure;

        var payment = await _payments.GetByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);
        return payment is null
            ? ApiResults.Fail<PaymentDto>(BaseApiResponseCode.NotFound, "支付单不存在")
            : ApiResults.Ok(PaymentAssembler.Build(payment, string.Empty));
    }
}

/// <summary>支付单视图装配。</summary>
internal static class PaymentAssembler
{
    /// <summary>装配支付单视图。</summary>
    /// <param name="payment">支付单实体。</param>
    /// <param name="orderStatusName">订单状态中文名，可为空。</param>
    /// <returns>支付单视图。</returns>
    public static PaymentDto Build(PaymentOrder payment, string orderStatusName)
    {
        var message = payment.Status == PaymentStatuses.Paid
            ? $"支付成功（{PaymentStatuses.NameOf(payment.Status)}）"
            : $"请支付 {payment.Amount:0.00} 元";
        if (orderStatusName.Length > 0) message = $"{message}，订单状态：{orderStatusName}";

        return new PaymentDto(
            payment.PaymentNo, payment.OrderNo, payment.Amount,
            payment.Status, PaymentStatuses.NameOf(payment.Status),
            payment.Channel, PaymentChannels.NameOf(payment.Channel),
            payment.PaidAt is null ? string.Empty : PaymentAssembler.FormatTime(payment.PaidAt.Value),
            message);
    }

    /// <summary>把 UTC 时间转成展示字符串。</summary>
    /// <param name="utc">UTC 时间。</param>
    /// <returns>展示用字符串。</returns>
    public static string FormatTime(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
