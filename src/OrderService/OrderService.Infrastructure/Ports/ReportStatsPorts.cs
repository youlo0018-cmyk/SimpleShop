using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;
using OrderService.Domain.Ports;

namespace OrderService.Infrastructure.Ports;

/// <summary>走内网 HTTP 向支付服务要退款汇总。</summary>
/// <remarks>
/// <b>失败返回 0 而不是抛异常</b>：工作台有 8 个指标，退款金额只是其中一个。
/// 让它把整页拖成 500，代价远大于「少显示一个数字」——
/// 而支付服务真的挂了，它自己的日志里有完整堆栈，排查不受影响。
/// </remarks>
public sealed class HttpRefundStatsPort : IRefundStatsPort
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpRefundStatsPort> _logger;

    /// <summary>构造端口。</summary>
    /// <param name="http">指向支付服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpRefundStatsPort(HttpClient http, ILogger<HttpRefundStatsPort> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<decimal> SumApprovedAsync(
        DateTime from, DateTime to, long merchantId, long platformId, CancellationToken ct = default)
    {
        try
        {
            var body = new Query
            {
                From = from,
                To = to,
                MerchantId = merchantId,
                PlatformId = platformId
            };

            var response = await _http
                .PostAsJsonAsync("internal/payments/refunds/SumApproved", body, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("支付服务退款汇总失败：HTTP {Code}", (int)response.StatusCode);
                return 0m;
            }

            var result = await response.Content
                .ReadFromJsonAsync<ApiResponse<Result>>(ct).ConfigureAwait(false);

            if (result is null || !result.Success || result.Data is null)
            {
                _logger.LogError("支付服务退款汇总业务失败：{Message}", result?.Message ?? "(空响应)");
                return 0m;
            }

            return result.Data.Amount;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "调用支付服务退款汇总失败，退款金额按 0 计");
            return 0m;
        }
    }

    /// <summary>退款汇总请求体。</summary>
    private sealed class Query
    {
        /// <summary>区间起（含）。</summary>
        [JsonPropertyName("from")]
        public DateTime From { get; set; }

        /// <summary>区间止（不含）。</summary>
        [JsonPropertyName("to")]
        public DateTime To { get; set; }

        /// <summary>商户 Id，0 表示不限。</summary>
        [JsonPropertyName("merchantId")]
        public long MerchantId { get; set; }

        /// <summary>平台 Id，0 表示不限。</summary>
        [JsonPropertyName("platformId")]
        public long PlatformId { get; set; }
    }

    /// <summary>退款汇总响应体。</summary>
    private sealed class Result
    {
        /// <summary>退款金额合计。</summary>
        [JsonPropertyName("amount")]
        public decimal Amount { get; set; }
    }
}

/// <summary>走内网 HTTP 向库存服务要预警 SKU 数。</summary>
/// <remarks>失败返回 0，理由同 <see cref="HttpRefundStatsPort"/>。</remarks>
public sealed class HttpLowStockPort : ILowStockPort
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpLowStockPort> _logger;

    /// <summary>构造端口。</summary>
    /// <param name="http">指向库存服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpLowStockPort(HttpClient http, ILogger<HttpLowStockPort> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> CountLowStockAsync(
        long merchantId, long platformId, CancellationToken ct = default)
    {
        try
        {
            var body = new Query { MerchantId = merchantId, PlatformId = platformId };

            var response = await _http
                .PostAsJsonAsync("internal/inventory/LowStock/Count", body, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("库存服务预警数查询失败：HTTP {Code}", (int)response.StatusCode);
                return 0;
            }

            var result = await response.Content
                .ReadFromJsonAsync<ApiResponse<Result>>(ct).ConfigureAwait(false);

            if (result is null || !result.Success || result.Data is null)
            {
                _logger.LogError("库存服务预警数业务失败：{Message}", result?.Message ?? "(空响应)");
                return 0;
            }

            return result.Data.Count;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "调用库存服务预警数查询失败，预警数按 0 计");
            return 0;
        }
    }

    /// <summary>预警数请求体。</summary>
    private sealed class Query
    {
        /// <summary>商户 Id，0 表示不限。</summary>
        [JsonPropertyName("merchantId")]
        public long MerchantId { get; set; }

        /// <summary>平台 Id，0 表示不限。</summary>
        [JsonPropertyName("platformId")]
        public long PlatformId { get; set; }
    }

    /// <summary>预警数响应体。</summary>
    private sealed class Result
    {
        /// <summary>预警 SKU 数。</summary>
        [JsonPropertyName("count")]
        public int Count { get; set; }
    }
}
