using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;

namespace PaymentService.Application.Services;

/// <summary>订单服务端口：支付与退款都要按订单的权威数据判断。</summary>
public interface IOrderPort
{
    /// <summary>取支付 / 退款用的订单信息。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单信息；不存在或服务不可用返回 null。</returns>
    /// <remarks>
    /// <b>金额只能从这里来</b>（规格 10.1）：支付服务不接收客户端传入的金额。
    /// 返回 null 时调用方<b>必须拒绝操作</b>，不能当成「金额为 0」。
    /// </remarks>
    Task<OrderForPayment?> GetForPaymentAsync(string orderNo, CancellationToken ct = default);

    /// <summary>把订单标记为已退款。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="refundAmount">本次退款金额。</param>
    /// <param name="items">
    /// 本次退款的行明细。订单侧要用它写自己的退款台账（行级可退余额就是靠它算的），
    /// 不带的话订单详情会把已经退掉的单显示成全额可退。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    Task<bool> MarkRefundedAsync(
        string orderNo, decimal refundAmount,
        IReadOnlyList<RefundLineSnapshot>? items = null,
        CancellationToken ct = default);

    /// <summary>请求订单侧完成支付收尾（库存确认 / 积分实扣 / 券核销 / 订单转已支付）。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    /// <remarks>
    /// 支付服务只判断「钱收到了没有」，<b>不自己实现订单侧的副作用</b>：
    /// 那四步少做一步就是资损或超卖，复制一份迟早与订单服务漂移。
    /// </remarks>
    Task<bool> CompletePaymentAsync(string orderNo, CancellationToken ct = default);
}

/// <summary>退款的一行快照，供订单侧写自己的退款台账。</summary>
/// <param name="OrderItemId">订单行 Id。</param>
/// <param name="Quantity">本次退的件数。</param>
/// <param name="Amount">该行本次退款金额，两位小数。</param>
public sealed record RefundLineSnapshot(long OrderItemId, int Quantity, decimal Amount);

/// <summary>走内网 HTTP 调订单服务的实现。</summary>
public sealed class HttpOrderPort : IOrderPort
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpOrderPort> _logger;

    /// <summary>构造端口。</summary>
    /// <param name="http">指向订单服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpOrderPort(HttpClient http, ILogger<HttpOrderPort> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OrderForPayment?> GetForPaymentAsync(string orderNo, CancellationToken ct = default)
    {
        try
        {
            var response = await _http
                .PostAsJsonAsync("internal/orders/for-payment", new { orderNo }, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("查询订单 {OrderNo} 的支付信息失败，HTTP {Code}", orderNo, (int)response.StatusCode);
                return null;
            }

            var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<OrderForPayment>>(ct).ConfigureAwait(false);
            if (envelope is null || !envelope.Success || envelope.Data is null) return null;
            return envelope.Data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "调用订单服务查询 {OrderNo} 的支付信息异常", orderNo);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> MarkRefundedAsync(
        string orderNo, decimal refundAmount,
        IReadOnlyList<RefundLineSnapshot>? items = null,
        CancellationToken ct = default)
    {
        try
        {
            // 把行明细一起送过去：订单侧要用它写 order_refund / order_refund_item，
            // 而「行级可退余额」正是从那张表算出来的。不带明细的话，
            // 后台订单详情会把已经退掉的单显示成全额可退。
            var payload = new
            {
                orderNo,
                refundAmount,
                items = (items ?? []).Select(a => new
                {
                    orderItemId = a.OrderItemId,
                    quantity = a.Quantity,
                    amount = a.Amount,
                }).ToArray(),
            };

            var response = await _http
                .PostAsJsonAsync("internal/orders/mark-refunded", payload, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("把订单 {OrderNo} 标记为已退款失败，HTTP {Code}", orderNo, (int)response.StatusCode);
                return false;
            }

            var envelope = await response.Content.ReadFromJsonAsync<ApiResponse>(ct).ConfigureAwait(false);
            return envelope is not null && envelope.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "调用订单服务标记 {OrderNo} 已退款异常", orderNo);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> CompletePaymentAsync(string orderNo, CancellationToken ct = default)
    {
        try
        {
            var response = await _http
                .PostAsJsonAsync("internal/orders/complete-payment", new { orderNo }, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("请求订单 {OrderNo} 完成支付收尾失败，HTTP {Code}", orderNo, (int)response.StatusCode);
                return false;
            }

            var envelope = await response.Content.ReadFromJsonAsync<ApiResponse>(ct).ConfigureAwait(false);
            return envelope is not null && envelope.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "调用订单服务完成 {OrderNo} 支付收尾异常", orderNo);
            return false;
        }
    }
}
