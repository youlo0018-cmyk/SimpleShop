using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;

namespace MarketingService.Application.Services;

/// <summary>订单服务端口：秒杀抢购下单。</summary>
public interface IOrderPort
{
    /// <summary>用秒杀价下单。</summary>
    /// <param name="request">抢购下单参数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下单结果，含订单号与实付金额。</returns>
    Task<SeckillOrderResult> CreateSeckillOrderAsync(SeckillOrderRequest request, CancellationToken ct = default);
}

/// <summary>秒杀下单参数。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="PlatformId">平台 Id。</param>
/// <param name="MerchantId">商户 Id。</param>
/// <param name="IdempotencyKey">幂等键。用限购业务单号，保证重复提交返回同一张单。</param>
/// <param name="ReceiverName">收货人。</param>
/// <param name="ReceiverPhone">收货电话。</param>
/// <param name="ReceiverAddress">收货地址。</param>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Quantity">数量，秒杀固定 1。</param>
/// <param name="SeckillPrice">秒杀价。</param>
/// <param name="ProductName">商品名快照。</param>
/// <param name="SkuSpecText">规格文本快照。</param>
/// <param name="DeliveryType">配送方式。</param>
/// <param name="CouponId">使用的券 Id，0 表示不用。</param>
/// <param name="PointsToUse">抵扣积分数，0 表示不用。</param>
/// <param name="Freight">运费。</param>
/// <param name="Remark">备注。</param>
public sealed record SeckillOrderRequest(
    long CustomerId, long PlatformId, long MerchantId,
    string IdempotencyKey,
    string ReceiverName, string ReceiverPhone, string ReceiverAddress,
    long SpuId, long SkuId, int Quantity,
    decimal SeckillPrice, string ProductName, string SkuSpecText,
    int DeliveryType = 1,
    long CouponId = 0, long PointsToUse = 0,
    decimal Freight = 0m, string Remark = "");

/// <summary>秒杀下单结果。</summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="OrderId">订单 Id。</param>
/// <param name="OrderNo">订单号。</param>
/// <param name="PayableAmount">实付金额。</param>
/// <param name="Message">失败原因或提示。</param>
public sealed record SeckillOrderResult(
    bool Succeeded, long OrderId, string OrderNo, decimal PayableAmount, string Message);

/// <summary>走内网 HTTP 调订单服务的秒杀下单实现。</summary>
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
    public async Task<SeckillOrderResult> CreateSeckillOrderAsync(
        SeckillOrderRequest request, CancellationToken ct = default)
    {
        var body = new
        {
            request.CustomerId, request.PlatformId, request.MerchantId, request.IdempotencyKey,
            request.ReceiverName, request.ReceiverPhone, request.ReceiverAddress,
            request.SpuId, request.SkuId, request.Quantity, request.SeckillPrice,
            request.ProductName, request.SkuSpecText, request.DeliveryType,
            request.CouponId, request.PointsToUse, request.Freight, request.Remark
        };

        try
        {
            var response = await _http
                .PostAsJsonAsync("internal/orders/seckill-create", body, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("秒杀下单接口返回 HTTP {Code}", (int)response.StatusCode);
                return new SeckillOrderResult(false, 0, string.Empty, 0m, "下单服务不可用");
            }

            var envelope = await response.Content
                .ReadFromJsonAsync<ApiResponse<OrderCreated>>(ct).ConfigureAwait(false);

            if (envelope is null || !envelope.Success || envelope.Data is null)
            {
                return new SeckillOrderResult(
                    false, 0, string.Empty, 0m,
                    envelope?.Message ?? "下单失败，请稍后重试");
            }

            return new SeckillOrderResult(
                true, envelope.Data.OrderId, envelope.Data.OrderNo, envelope.Data.PayableAmount, "抢购成功");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "调用订单服务秒杀下单异常");
            return new SeckillOrderResult(false, 0, string.Empty, 0m, "下单服务调用失败");
        }
    }

    /// <summary>订单创建响应数据。</summary>
    /// <param name="OrderId">订单 Id。</param>
    /// <param name="OrderNo">订单号。</param>
    /// <param name="PayableAmount">实付金额。</param>
    private sealed record OrderCreated(
        [property: JsonPropertyName("orderId")] long OrderId,
        [property: JsonPropertyName("orderNo")] string OrderNo,
        [property: JsonPropertyName("payableAmount")] decimal PayableAmount);
}