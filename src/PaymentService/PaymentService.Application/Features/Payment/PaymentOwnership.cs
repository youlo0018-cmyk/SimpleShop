using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using PaymentService.Application.Services;

namespace PaymentService.Application.Features.Payment;

/// <summary>支付侧的对象归属校验（防 IDOR）。</summary>
/// <remarks>
/// <para><b>为什么必须有</b>：支付接口的参数只有<b>订单号</b>，而订单号是可枚举的字符串。
/// 不校验归属的话，任何登录客户都能把<b>别人的订单</b>标成已支付
/// （<c>/payments/Confirm</c> 是 C 端路径，网关按客户令牌放行）——
/// 与之前 <c>/payments/Simulate</c> 那个洞同一形状，只是入口不同。</para>
///
/// <para>客户令牌存在时以令牌里的客户为准（<see cref="TenantContextHolder"/>），
/// 与服务直连 / 内部任务（无客户上下文）保持兼容 —— 那些调用方本来就持有后台令牌或走内网。</para>
/// </remarks>
internal static class PaymentOwnership
{
    /// <summary>校验「这笔单是不是当前登录客户的」。</summary>
    /// <param name="order">已取回的订单（调用方通常已经查过，避免重复跨服务调用）。</param>
    /// <returns>越权时返回失败响应；通过返回 null。</returns>
    internal static ApiResponse<PaymentDto>? RejectIfNotOwner(OrderForPayment order)
    {
        var context = TenantContextHolder.Current;
        if (!context.IsCustomer) return null;
        if (context.UserId == order.CustomerId) return null;

        return ApiResults.Fail<PaymentDto>(
            BaseApiResponseCode.Forbidden, "不能操作其他客户的订单");
    }

    /// <summary>按订单号取订单并校验归属。</summary>
    /// <param name="orders">订单端口。</param>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>校验通过时失败响应为 null（<c>Order</c> 为取回的订单）；否则失败响应非空。</returns>
    internal static async Task<(OrderForPayment? Order, ApiResponse<PaymentDto>? Failure)> LoadOwnedAsync(
        IOrderPort orders, string orderNo, CancellationToken ct)
    {
        var order = await orders.GetForPaymentAsync(orderNo.Trim(), ct).ConfigureAwait(false);
        if (order is null)
        {
            return (null, ApiResults.Fail<PaymentDto>(
                BaseApiResponseCode.NotFound, "订单不存在或订单服务不可用"));
        }

        return (order, RejectIfNotOwner(order));
    }
}
