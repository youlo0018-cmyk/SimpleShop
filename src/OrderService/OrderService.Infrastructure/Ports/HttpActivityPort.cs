using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OrderService.Domain.Ports;

namespace OrderService.Infrastructure.Ports;

/// <summary>走内网 HTTP 调营销服务，按行试算活动优惠。</summary>
public sealed class HttpActivityPort : IActivityPort
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpActivityPort> _logger;

    /// <summary>构造端口。</summary>
    /// <param name="http">指向营销服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpActivityPort(HttpClient http, ILogger<HttpActivityPort> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(long SkuId, decimal ActivityDiscount)>> QuoteAsync(
        long customerId, long platformId, long sessionId, long couponId,
        IReadOnlyList<(long SpuId, long SkuId, decimal Amount)> lines,
        string orderNo = "", CancellationToken ct = default)
    {
        if (lines.Count == 0) return [];

        try
        {
            var body = new QuoteRequest(
                customerId,
                lines.Select(a => new QuoteLine(a.SpuId, a.SkuId, a.Amount)).ToArray(),
                platformId, sessionId, couponId, orderNo);

            var response = await _http.PostAsJsonAsync(
                "internal/marketing/activities/Quote", body, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("活动试算接口 HTTP {Code}，本单按无活动优惠处理",
                    (int)response.StatusCode);
                return [];
            }

            var payload = await response.Content
                .ReadFromJsonAsync<QuoteResponse>(ct).ConfigureAwait(false);

            if (payload is null || !payload.Success || payload.Data is null)
            {
                _logger.LogWarning("活动试算未返回可用结果，本单按无活动优惠处理");
                return [];
            }

            return payload.Data.Lines
                .Select(a => (a.SkuId, a.ActivityDiscount))
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 营销服务挂了就当没有活动优惠，**不能让下单整个失败**：
            // 实付金额仍由本服务按行金额算，少折扣只是少优惠，多算才是资损。
            _logger.LogError(ex, "调用营销服务活动试算失败，本单按无活动优惠处理");
            return [];
        }
    }

    /// <summary>请求体。</summary>
    /// <param name="CustomerId">客户 Id。</param>
    /// <param name="Lines">订单行。</param>
    /// <param name="PlatformId">平台 Id。</param>
    /// <param name="SessionId">秒杀场次 Id。</param>
    /// <param name="CouponId">已选券 Id。</param>
    /// <param name="OrderNo">
    /// 订单号。营销服务靠它把满赠的发放承诺按订单落库；结算试算传空串。
    /// </param>
    private sealed record QuoteRequest(
        long CustomerId, QuoteLine[] Lines, long PlatformId, long SessionId, long CouponId, string OrderNo);

    /// <summary>订单行。</summary>
    /// <param name="SpuId">SPU Id。</param>
    /// <param name="SkuId">SKU Id。</param>
    /// <param name="Amount">行金额。</param>
    private sealed record QuoteLine(long SpuId, long SkuId, decimal Amount);

    /// <summary>响应信封。</summary>
    /// <param name="Success">是否成功。</param>
    /// <param name="Data">试算结果。</param>
    private sealed record QuoteResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("data")] QuoteData? Data);

    /// <summary>试算结果。</summary>
    /// <param name="Lines">逐行优惠。</param>
    /// <param name="ActivityDiscountTotal">活动优惠合计。</param>
    private sealed record QuoteData(
        [property: JsonPropertyName("lines")] QuoteLineResult[] Lines,
        [property: JsonPropertyName("activityDiscountTotal")] decimal ActivityDiscountTotal);

    /// <summary>逐行优惠。</summary>
    /// <param name="SkuId">SKU Id。</param>
    /// <param name="ActivityDiscount">该行活动优惠额。</param>
    private sealed record QuoteLineResult(
        [property: JsonPropertyName("skuId")] long SkuId,
        [property: JsonPropertyName("activityDiscount")] decimal ActivityDiscount);
}
