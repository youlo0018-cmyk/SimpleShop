using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace ProductService.Application.Services;

/// <summary>库存服务客户端（ProductService → InventoryService）。</summary>
/// <remarks>
/// 商品创建时要把每个 SKU 的初始库存送到库存服务建记录（DATA_SPEC 5.7.2「创建时初始化库存记录」）。
/// 库存**存在库存服务自己的库里**，商品这边不留副本——两边都存就是两份真相，迟早对不上。
/// </remarks>
public interface IInventoryClient
{
    /// <summary>初始化某个 SKU 的库存。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="quantity">初始库存数量。</param>
    /// <param name="productName">商品名（库存列表冗余显示用）。</param>
    /// <param name="skuSpecText">规格文本（库存列表冗余显示用）。</param>
    /// <param name="warnThreshold">库存预警阈值。</param>
    /// <param name="bizNo">业务单号，用 SKU 的编码保证幂等。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 表示库存已就绪；false 表示失败，调用方应当拒绝本次保存。</returns>
    Task<bool> InitAsync(
        long skuId,
        int quantity,
        string productName,
        string skuSpecText,
        int warnThreshold,
        string bizNo,
        CancellationToken ct = default);
}

/// <summary>走内网 HTTP 调用库存服务的实现。</summary>
public sealed class HttpInventoryClient : IInventoryClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpInventoryClient> _logger;

    /// <summary>构造客户端。</summary>
    /// <param name="http">指向库存服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpInventoryClient(HttpClient http, ILogger<HttpInventoryClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> InitAsync(
        long skuId,
        int quantity,
        string productName,
        string skuSpecText,
        int warnThreshold,
        string bizNo,
        CancellationToken ct = default)
    {
        var payload = new
        {
            skuId,
            quantity,
            productName,
            skuSpecText,
            warnThreshold,
            bizNo
        };

        try
        {
            var response = await _http.PostAsJsonAsync("internal/inventory/Init", payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "SKU {SkuId} 初始化库存失败，库存服务返回 {Status}，商品保存将回退",
                    skuId, (int)response.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // 库存服务不可用时必须让商品保存失败：一个没有库存记录的 SKU 是买不了的，
            // 静默放过会留下一批「看起来正常、实际永远缺货」的商品，比直接报错难查得多。
            _logger.LogError(ex, "SKU {SkuId} 初始化库存调用异常，商品保存将回退", skuId);
            return false;
        }
    }
}