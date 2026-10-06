using System.Net.Http.Json;
using Collaboration.Domain.Common;
using CustomerService.Application.Services;
using Microsoft.Extensions.Logging;

namespace CustomerService.Infrastructure.Services;

/// <summary>走内网 HTTP 调商品服务，批量取收藏页摘要。</summary>
public sealed class HttpProductSummaryClient : IProductSummaryClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpProductSummaryClient> _logger;

    /// <summary>构造客户端。</summary>
    /// <param name="http">指向商品服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpProductSummaryClient(HttpClient http, ILogger<HttpProductSummaryClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, ProductSummary>> GetSpuSummariesAsync(
        IReadOnlyCollection<long> spuIds, CancellationToken ct = default)
    {
        if (spuIds.Count == 0)
        {
            return new Dictionary<long, ProductSummary>();
        }

        try
        {
            var query = string.Join(',', spuIds.Distinct().Take(50));
            var response = await _http
                .GetAsync($"internal/products/spu-summaries?spuIds={query}", ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content
                .ReadFromJsonAsync<ApiResponse<List<ProductSummary>>>(ct)
                .ConfigureAwait(false);

            if (body is null || !body.Success || body.Data is null)
            {
                _logger.LogError("商品服务返回异常：{Message}", body?.Message ?? "(空响应)");
                return new Dictionary<long, ProductSummary>();
            }

            return body.Data.ToDictionary(a => a.SpuId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 收藏页可以只显示「商品 Id + 收藏时间」；商品服务抖动不该让整页 500。
            _logger.LogError(ex, "批量取商品摘要失败，收藏页回退为 Id 展示");
            return new Dictionary<long, ProductSummary>();
        }
    }
}
