using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;

namespace CartService.Application.Services;

/// <summary>商品服务客户端（购物车要拿 SKU 快照）。</summary>
public interface IProductClient
{
    /// <summary>按 SKU Id 集合取快照信息。</summary>
    /// <param name="skuIds">SKU Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的快照；未命中的 SKU 不会出现在结果里。</returns>
    Task<IReadOnlyList<SkuSnapshot>> GetSkuSnapshotsAsync(IReadOnlyCollection<long> skuIds, CancellationToken ct = default);
}

/// <summary>SKU 快照。字段与 ProductService 的内部接口保持一致。</summary>
public sealed record SkuSnapshot(
    [property: JsonPropertyName("skuId")] long SkuId,
    [property: JsonPropertyName("productId")] long ProductId,
    [property: JsonPropertyName("skuCode")] string SkuCode,
    [property: JsonPropertyName("skuName")] string SkuName,
    [property: JsonPropertyName("skuSpecText")] string SkuSpecText,
    [property: JsonPropertyName("price")] decimal Price,
    [property: JsonPropertyName("originalPrice")] decimal OriginalPrice,
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("status")] int Status);

/// <summary>走内网 HTTP 调用商品服务的实现。</summary>
public sealed class HttpProductClient : IProductClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpProductClient> _logger;

    /// <summary>构造客户端。</summary>
    /// <param name="http">指向商品服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpProductClient(HttpClient http, ILogger<HttpProductClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SkuSnapshot>> GetSkuSnapshotsAsync(
        IReadOnlyCollection<long> skuIds, CancellationToken ct = default)
    {
        if (skuIds.Count == 0) return Array.Empty<SkuSnapshot>();

        var query = string.Join(',', skuIds);
        var response = await _http.GetAsync($"internal/products/skus?skuIds={query}", ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<SkuSnapshot>>>(ct);
        if (body is null || !body.Success || body.Data is null)
        {
            _logger.LogError("商品服务返回异常：{Message}", body?.Message ?? "(空响应)");
            return Array.Empty<SkuSnapshot>();
        }

        return body.Data;
    }
}