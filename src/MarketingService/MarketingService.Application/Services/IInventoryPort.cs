using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;

namespace MarketingService.Application.Services;

/// <summary>库存服务端口：秒杀只用它做<b>划出 / 回补</b>。</summary>
/// <remarks>
/// 秒杀抢购成功后<b>不再动常规库存</b>（货早就在发布时就划走了，BUSINESS.md 12.4），
/// 所以这里只有两个动作，不提供 lock / deduct——把它们暴露出来只会让人写出会重复扣库存的代码。
/// </remarks>
public interface IInventoryPort
{
    /// <summary>把库存从常规池划到秒杀池。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="quantity">划出数量。</param>
    /// <param name="bizNo">业务单号，幂等键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true；库存不足返回 false。</returns>
    Task<bool> ReserveAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default);

    /// <summary>把剩余库存从秒杀池回补到常规池。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="quantity">回补数量。</param>
    /// <param name="bizNo">业务单号，幂等键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    Task<bool> ReleaseAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default);
}

/// <summary>走内网 HTTP 调库存服务的实现。</summary>
public sealed class HttpInventoryPort : IInventoryPort
{
    /// <summary>库存动作名：发布场次划出。</summary>
    private const string ReserveAction = "seckill_reserve";

    /// <summary>库存动作名：结束场次回补。</summary>
    private const string ReleaseAction = "seckill_release";

    private readonly HttpClient _http;
    private readonly ILogger<HttpInventoryPort> _logger;

    /// <summary>构造端口。</summary>
    /// <param name="http">指向库存服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpInventoryPort(HttpClient http, ILogger<HttpInventoryPort> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> ReserveAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default)
        => ApplyAsync(skuId, quantity, bizNo, ReserveAction, ct);

    /// <inheritdoc />
    public Task<bool> ReleaseAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default)
        => ApplyAsync(skuId, quantity, bizNo, ReleaseAction, ct);

    /// <summary>调用一次库存变更。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="quantity">数量。</param>
    /// <param name="bizNo">业务单号（幂等键）。</param>
    /// <param name="action">动作名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true；划出时库存不足返回 false；其它失败抛异常。</returns>
    /// <remarks>
    /// <b>回补失败要抛异常，划出时库存不足返回 false</b>——两者语义相反：
    /// 划出时库存不足是「这个商品卖不了这场」，跳过即可；
    /// 回补失败是「货没还回常规库存」，必须让人知道，否则库存凭空蒸发。
    /// </remarks>
    private async Task<bool> ApplyAsync(
        long skuId, int quantity, string bizNo, string action, CancellationToken ct)
    {
        var payload = new ApplyStockRequest(skuId, action, quantity, bizNo, "秒杀场次", 0, 0);

        var response = await _http
            .PostAsJsonAsync("internal/inventory/Apply", payload, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("库存 {Action} 调用失败：SKU {SkuId} HTTP {Code}", action, skuId, (int)response.StatusCode);
            throw new InvalidOperationException(
                $"库存服务调用失败（{action} / SKU {skuId} / HTTP {(int)response.StatusCode}）");
        }

        var body = await response.Content
            .ReadFromJsonAsync<ApiResponse<StockChangeResult>>(ct).ConfigureAwait(false);

        if (body is null || !body.Success)
        {
            // 库存不足在划出时是正常的「卖不了」，返回 false 让上层跳过这个商品；
            // 回补时走到这里则是真故障，同样抛出去，由上层记入失败清单
            if (action == ReserveAction)
            {
                _logger.LogWarning("SKU {SkuId} 划出 {Qty} 件失败：{Message}", skuId, quantity, body?.Message);
                return false;
            }

            throw new InvalidOperationException($"库存回补失败：{body?.Message ?? "(空响应)"}");
        }

        return true;
    }

    /// <summary>库存变更请求体。</summary>
    /// <param name="SkuId">SKU Id。</param>
    /// <param name="Action">动作名。</param>
    /// <param name="Quantity">数量。</param>
    /// <param name="BizNo">业务单号（幂等键）。</param>
    /// <param name="Remark">备注。</param>
    /// <param name="PlatformId">平台 Id。</param>
    /// <param name="MerchantId">商户 Id。</param>
    private sealed record ApplyStockRequest(
        [property: JsonPropertyName("skuId")] long SkuId,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("quantity")] int Quantity,
        [property: JsonPropertyName("bizNo")] string BizNo,
        [property: JsonPropertyName("remark")] string Remark,
        [property: JsonPropertyName("platformId")] long PlatformId,
        [property: JsonPropertyName("merchantId")] long MerchantId);

    /// <summary>库存变更结果。</summary>
    /// <param name="SkuId">SKU Id。</param>
    /// <param name="Available">变更后可用。</param>
    /// <param name="Locked">变更后锁定。</param>
    /// <param name="Deducted">变更后已扣减。</param>
    /// <param name="AlreadyApplied">是否命中幂等。</param>
    private sealed record StockChangeResult(
        [property: JsonPropertyName("skuId")] long SkuId,
        [property: JsonPropertyName("available")] int Available,
        [property: JsonPropertyName("locked")] int Locked,
        [property: JsonPropertyName("deducted")] int Deducted,
        [property: JsonPropertyName("alreadyApplied")] bool AlreadyApplied);
}

/// <summary>商品服务端口：加秒杀商品时取 SKU 快照与售价。</summary>
/// <remarks>
/// 与 <see cref="IInventoryPort"/> 分开是因为它们打的是<b>两个不同的服务</b>：
/// 库存划出去库存服务，拿商品名与售价去商品服务。合成一个端口的话，
/// BaseAddress 就只能指向其中一个，另一个调用必然 404。
/// </remarks>
public interface IProductPort
{
    /// <summary>按 SKU Id 集合取快照。</summary>
    /// <param name="skuIds">SKU Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>SKU Id → 快照；查不到的不在结果里。</returns>
    Task<IReadOnlyDictionary<long, SkuSnapshot>> GetSkuSnapshotsAsync(
        IReadOnlyCollection<long> skuIds, CancellationToken ct = default);
}

/// <summary>SKU 快照，字段与 ProductService 内部接口一致。</summary>
/// <param name="SkuId">SKU Id。</param>
/// <param name="SpuId">SPU Id。</param>
/// <param name="ProductName">商品名。</param>
/// <param name="SkuSpecText">规格文本。</param>
/// <param name="Image">SKU 图。</param>
/// <param name="Price">售价。</param>
public sealed record SkuSnapshot(
    [property: JsonPropertyName("skuId")] long SkuId,
    [property: JsonPropertyName("productId")] long SpuId,
    [property: JsonPropertyName("skuName")] string ProductName,
    [property: JsonPropertyName("skuSpecText")] string SkuSpecText,
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("price")] decimal Price);

/// <summary>走内网 HTTP 调商品服务的实现。</summary>
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
    public async Task<IReadOnlyDictionary<long, SkuSnapshot>> GetSkuSnapshotsAsync(
        IReadOnlyCollection<long> skuIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, SkuSnapshot>();
        if (skuIds.Count == 0) return result;

        var query = string.Join(',', skuIds);
        var response = await _http
            .GetAsync($"internal/products/skus?skuIds={query}", ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("取 SKU 快照失败：HTTP {Code}", (int)response.StatusCode);
            return result;
        }

        var body = await response.Content
            .ReadFromJsonAsync<ApiResponse<List<SkuSnapshot>>>(ct).ConfigureAwait(false);

        if (body is null || !body.Success || body.Data is null) return result;

        foreach (var sku in body.Data)
        {
            result[sku.SkuId] = sku;
        }

        return result;
    }
}