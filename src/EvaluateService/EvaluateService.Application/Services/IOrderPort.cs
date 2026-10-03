using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;

namespace EvaluateService.Application.Services;

/// <summary>订单服务端口：评价必须建立在「真实买过且已完成」的订单上。</summary>
public interface IOrderPort
{
    /// <summary>取评价所需的订单信息。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单信息；订单不存在返回 null。</returns>
    Task<OrderForEvaluate?> GetForEvaluateAsync(string orderNo, CancellationToken ct = default);
}

/// <summary>评价用的订单信息。</summary>
/// <param name="OrderId">订单 Id。</param>
/// <param name="OrderNo">订单号。</param>
/// <param name="CustomerId">下单客户 Id。</param>
/// <param name="PlatformId">平台 Id。</param>
/// <param name="MerchantId">商户 Id。</param>
/// <param name="Status">订单状态。</param>
/// <param name="CanEvaluate">是否处于可评价状态（已完成）。由订单服务判定，评价服务不复制状态常量。</param>
/// <param name="Items">该订单买过的 SPU 及其 SKU 清单。</param>
public sealed record OrderForEvaluate(
    long OrderId, string OrderNo, long CustomerId, long PlatformId, long MerchantId,
    int Status, bool CanEvaluate, IReadOnlyList<OrderSpuForEvaluate> Items);

/// <summary>订单内的单个 SPU 及其 SKU。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SpuName">商品名快照。</param>
/// <param name="Skus">该 SPU 下本单购买的 SKU 清单。</param>
public sealed record OrderSpuForEvaluate(
    long SpuId, string SpuName, IReadOnlyList<OrderSkuForEvaluate> Skus);

/// <summary>订单内的单个 SKU。</summary>
/// <param name="SkuId">SKU Id。</param>
/// <param name="SkuSpecText">规格文本快照。</param>
/// <param name="OrderItemId">订单行 Id。</param>
public sealed record OrderSkuForEvaluate(long SkuId, string SkuSpecText, long OrderItemId);

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
    public async Task<OrderForEvaluate?> GetForEvaluateAsync(string orderNo, CancellationToken ct = default)
    {
        try
        {
            var response = await _http
                .PostAsJsonAsync("internal/orders/for-evaluate", new { orderNo }, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("查询评价用订单信息失败，订单 {OrderNo} 返回 HTTP {Code}",
                    orderNo, (int)response.StatusCode);
                return null;
            }

            var envelope = await response.Content
                .ReadFromJsonAsync<ApiResponse<OrderForEvaluate>>(ct)
                .ConfigureAwait(false);

            if (envelope is null || !envelope.Success || envelope.Data is null) return null;

            return envelope.Data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "调用订单服务查询评价用订单信息异常，订单 {OrderNo}", orderNo);
            return null;
        }
    }
}
