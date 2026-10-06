using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;
using OrderService.Domain.Ports;

namespace OrderService.Infrastructure.Ports;

/// <summary>走内网 HTTP 调商品服务，回查 SKU 的权威售价与可售状态。</summary>
public sealed class HttpProductPort : IProductPort
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpProductPort> _logger;

    /// <summary>构造端口。</summary>
    /// <param name="http">指向商品服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpProductPort(HttpClient http, ILogger<HttpProductPort> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, SkuPriceInfo>> GetSkuPricesAsync(
        IReadOnlyCollection<long> skuIds, CancellationToken ct = default)
    {
        if (skuIds.Count == 0) return new Dictionary<long, SkuPriceInfo>();

        const string path = "internal/products/skus/pricing";
        var url = $"{path}?skuIds={string.Join(',', skuIds)}";

        HttpResponseMessage http;
        try
        {
            http = await _http.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "回查商品服务定价失败（网络异常）");
            throw new OrderDownstreamException("商品服务", path, ex.Message);
        }

        if (!http.IsSuccessStatusCode)
        {
            _logger.LogError("商品服务 {Path} 返回 HTTP {Code}", path, (int)http.StatusCode);
            throw new OrderDownstreamException("商品服务", path, $"HTTP {(int)http.StatusCode}");
        }

        // 内部接口同样走统一的 {success,data} 信封，直接反序列化成 List 会报
        // 「无法转换成 List」，那是个极具误导性的报错。
        ApiResponse<List<SkuPricingResponse>>? body;
        try
        {
            body = await http.Content.ReadFromJsonAsync<ApiResponse<List<SkuPricingResponse>>>(ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "商品服务 {Path} 的响应无法解析", path);
            throw new OrderDownstreamException("商品服务", path, "响应无法解析");
        }

        if (body is null || !body.Success)
        {
            _logger.LogError("商品服务 {Path} 失败（{Code}）：{Message}",
                path, body?.Code, body?.Message);
            throw new OrderDownstreamException("商品服务", path, body?.Message ?? "响应为空");
        }

        // 查不到的 SKU **不会**出现在结果里，由调用方区分「不存在」与「已下架」。
        // 刻意**不**用客户端报的价格兜底：那等于把后门重新打开。
        var result = new Dictionary<long, SkuPriceInfo>();
        foreach (var a in body.Data ?? [])
        {
            result[a.SkuId] = new SkuPriceInfo(
                a.SkuId, a.ProductId, a.Price, a.SkuEnabled == 1,
                a.SpuApproved, a.SpuOnShelf, a.MerchantId, a.PlatformId, a.DeliveryType,
                a.SkuName, a.SkuSpecText, a.Image);
        }

        return result;
    }

    /// <summary>商品服务的定价响应。</summary>
    /// <param name="SkuId">SKU Id。</param>
    /// <param name="ProductId">所属 SPU Id。</param>
    /// <param name="Price">售价。</param>
    /// <param name="SkuEnabled">SKU 是否启用，1 启用 / 2 停用。</param>
    /// <param name="SpuApproved">SPU 是否审核通过。</param>
    /// <param name="SpuOnShelf">SPU 是否已上架。</param>
    /// <param name="MerchantId">归属商户 Id。</param>
    /// <param name="PlatformId">归属平台 Id。</param>
    /// <param name="DeliveryType">配送方式，挂在 SPU 上。</param>
    /// <param name="SkuName">商品名的权威快照。</param>
    /// <param name="SkuSpecText">规格文本的权威快照。</param>
    /// <param name="Image">SKU 图。</param>
    private sealed record SkuPricingResponse(
        [property: JsonPropertyName("skuId")] long SkuId,
        [property: JsonPropertyName("productId")] long ProductId,
        [property: JsonPropertyName("price")] decimal Price,
        [property: JsonPropertyName("skuEnabled")] int SkuEnabled,
        [property: JsonPropertyName("spuApproved")] bool SpuApproved,
        [property: JsonPropertyName("spuOnShelf")] bool SpuOnShelf,
        [property: JsonPropertyName("merchantId")] long MerchantId,
        [property: JsonPropertyName("platformId")] long PlatformId,
        [property: JsonPropertyName("deliveryType")] int DeliveryType,
        [property: JsonPropertyName("skuName")] string SkuName,
        [property: JsonPropertyName("skuSpecText")] string SkuSpecText,
        [property: JsonPropertyName("image")] string Image);
}
