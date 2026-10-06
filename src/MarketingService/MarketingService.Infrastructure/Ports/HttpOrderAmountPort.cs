using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using MarketingService.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MarketingService.Infrastructure.Ports;

/// <summary>走内网 HTTP 向订单服务要成交额 / 参与金额。</summary>
/// <remarks>
/// 失败返回 0 而不是抛异常：金额只是报表里的一个数字，
/// 让它把整张报表拖成 500 代价太大；真实故障由订单服务自己的日志暴露。
/// </remarks>
public sealed class HttpOrderAmountPort : IOrderAmountPort
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpOrderAmountPort> _logger;

    /// <summary>构造端口。</summary>
    /// <param name="http">指向订单服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpOrderAmountPort(HttpClient http, ILogger<HttpOrderAmountPort> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<decimal> SumPayableAsync(
        IReadOnlyCollection<string> orderNos, CancellationToken ct = default)
    {
        if (orderNos is null || orderNos.Count == 0) return 0m;

        try
        {
            var body = new Query { OrderNos = orderNos.ToList() };

            var response = await _http
                .PostAsJsonAsync("internal/orders/sum-payable", body, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("订单服务金额汇总失败：HTTP {Code}", (int)response.StatusCode);
                return 0m;
            }

            var result = await response.Content
                .ReadFromJsonAsync<ApiResponse<Result>>(ct).ConfigureAwait(false);

            if (result is null || !result.Success || result.Data is null)
            {
                _logger.LogError("订单服务金额汇总业务失败：{Message}", result?.Message ?? "(空响应)");
                return 0m;
            }

            return result.Data.Amount;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "调用订单服务金额汇总失败，金额按 0 计");
            return 0m;
        }
    }

    /// <summary>成交额汇总请求体。</summary>
    private sealed class Query
    {
        /// <summary>订单号集合。</summary>
        [JsonPropertyName("orderNos")]
        public List<string> OrderNos { get; set; } = new();
    }

    /// <summary>成交额汇总响应体。</summary>
    private sealed class Result
    {
        /// <summary>成交额合计。</summary>
        [JsonPropertyName("amount")]
        public decimal Amount { get; set; }
    }
}
