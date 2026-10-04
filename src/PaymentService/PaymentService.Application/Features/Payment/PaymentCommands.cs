using Collaboration.Domain.Common;
using MediatR;

namespace PaymentService.Application.Features.Payment;

/// <summary>创建支付单。</summary>
/// <remarks>
/// 🔴 <b>命令里没有金额字段</b>：金额一律由服务端反查订单实付（规格 10.1）。
/// 客户端报多少不是关键，「这笔单子实际该付多少」才关键——
/// 金额由服务端算，才不会出现「改一下前端就能少付」。
/// </remarks>
/// <param name="OrderNo">订单号。</param>
public record CreatePaymentCommand(string OrderNo) : IRequest<ApiResponse<PaymentDto>>;

/// <summary>确认支付（C 端：用户在支付页点了「确认支付」）。</summary>
/// <param name="OrderNo">订单号。</param>
public record ConfirmPaymentCommand(string OrderNo) : IRequest<ApiResponse<PaymentDto>>;

/// <summary>后台模拟支付（订单列表每行的按钮）。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="Success">true 支付成功 / false 支付失败。</param>
public record SimulatePaymentCommand(string OrderNo, bool Success) : IRequest<ApiResponse<PaymentDto>>;

/// <summary>查支付单。</summary>
/// <param name="OrderNo">订单号。</param>
public record QueryPaymentCommand(string OrderNo) : IRequest<ApiResponse<PaymentDto>>;

/// <summary>支付单视图。</summary>
/// <param name="PaymentNo">支付单号。</param>
/// <param name="OrderNo">订单号。</param>
/// <param name="Amount">应付金额，服务端反查。</param>
/// <param name="Status">支付单状态。</param>
/// <param name="StatusName">支付单状态中文名。</param>
/// <param name="Channel">支付渠道。</param>
/// <param name="ChannelName">支付渠道中文名。</param>
/// <param name="PaidAt">支付成功时间，未支付为空。</param>
/// <param name="Message">给用户看的提示。</param>
public sealed record PaymentDto(string PaymentNo, string OrderNo, decimal Amount, int Status, string StatusName, int Channel, string ChannelName, string PaidAt, string Message);
