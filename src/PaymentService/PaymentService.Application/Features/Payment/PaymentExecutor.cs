using Collaboration.Domain.Common;
using PaymentService.Application.Services;
using PaymentService.Domain.Entities;
using PaymentService.Domain.IRepository;
using PaymentService.Domain.Services;
using Microsoft.Extensions.Logging;

namespace PaymentService.Application.Features.Payment;

/// <summary>支付收尾的公共执行逻辑（C 端确认与后台模拟共用）。</summary>
public static class PaymentExecutor
{
    /// <summary>执行一次支付。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="succeed">true 成功 / false 模拟失败。</param>
    /// <param name="payments">支付仓储。</param>
    /// <param name="orders">订单服务端口。</param>
    /// <param name="logger">日志器，可为空。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>支付单视图。</returns>
    /// <remarks>
    /// <b>支付幂等靠「期望状态」条件更新</b>，不是「先查再改」：两个并发确认都会查到
    /// 「待支付」然后都去改，于是订单侧收尾跑两次——积分实扣两次、库存确认两次、券核销两次。
    /// 带上「当前必须是待支付」，第二个请求拿到 0 行，就知道已经付过了。
    ///
    /// <b>先让订单侧收尾，成功了才把支付单置为已支付</b>。反过来做的话，
    /// 支付单显示已支付而订单侧收尾失败，用户会看到「已支付但订单没变化」，比失败更难解释。
    /// </remarks>
    public static async Task<ApiResponse<PaymentDto>> ExecuteAsync(
        string orderNo, bool succeed, IPaymentRepository payments, IOrderPort orders,
        ILogger? logger, CancellationToken ct)
    {
        var no = orderNo.Trim();

        var order = await orders.GetForPaymentAsync(no, ct).ConfigureAwait(false);
        if (order is null)
        {
            return ApiResults.Fail<PaymentDto>(BaseApiResponseCode.NotFound, "订单不存在或订单服务不可用");
        }

        var payment = await payments.GetByOrderNoAsync(no, ct).ConfigureAwait(false);
        if (payment is null)
        {
            return ApiResults.Fail<PaymentDto>(
                BaseApiResponseCode.NotFound, "支付单不存在，请先创建支付单");
        }

        if (payment.Status == PaymentStatuses.Paid)
        {
            // 幂等命中：重复确认直接返回成功，**不重复触发**订单侧收尾
            return ApiResults.Ok(PaymentAssembler.Build(payment, order.StatusName), "订单已支付");
        }

        if (!succeed)
        {
            // 支付失败**什么都不做**：订单保持 10 待支付，库存还锁着、积分还冻着、
            // 券还占着，用户可以重新发起（规格 10.1）。
            // 顺手把这些退了的话，模拟出来的行为跟真实支付完全相反
            logger?.LogInformation("模拟支付失败：订单 {OrderNo}，状态保持不变", no);
            return ApiResults.Ok(
                PaymentAssembler.Build(payment, order.StatusName), "已模拟支付失败，订单状态保持不变");
        }

        if (order.Status != OrderStatusNumbers.PendingPayment)
        {
            return ApiResults.Fail<PaymentDto>(
                BaseApiResponseCode.OrderStateInvalid,
                $"当前订单状态是「{order.StatusName}」，只有待支付的订单可以支付");
        }

        var completed = await orders.CompletePaymentAsync(no, ct).ConfigureAwait(false);
        if (!completed)
        {
            logger?.LogError("订单 {OrderNo} 支付收尾失败，支付单保持待支付", no);
            return ApiResults.Fail<PaymentDto>(
                BaseApiResponseCode.RemoteCallFailed, "订单服务处理失败，请稍后重试");
        }

        await payments.TryChangeStatusAsync(
            payment.Id, PaymentStatuses.Pending, PaymentStatuses.Paid,
            DateTime.UtcNow, string.Empty, ct).ConfigureAwait(false);

        var updated = await payments.GetByOrderNoAsync(no, ct).ConfigureAwait(false);
        return ApiResults.Ok(PaymentAssembler.Build(updated ?? payment, order.StatusName), "支付成功");
    }
}
