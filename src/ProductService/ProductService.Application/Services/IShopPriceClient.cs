using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;

namespace ProductService.Application.Services;

/// <summary>营销服务客户端：商品列表与详情的**到手价**。</summary>
/// <remarks>
/// 商品卡要展示「到手价 + 划线原价 + 优惠来源标签」（BUSINESS.md 11.5），
/// 所以前台每次展示商品都要问一次营销服务。价格规则放在营销服务里，
/// 商品服务**不复制**优惠算法——复制一份就会有两个地方算价，改规则只改一处必然对不上账。
/// </remarks>
public interface IShopPriceClient
{
    /// <summary>按 <b>SKU 逐个独立</b>试算到手价，一次调用算完所有 SKU。</summary>
    /// <param name="customerId">客户 Id；<b>传 0 表示游客</b>，只算活动价不计券。</param>
    /// <param name="items">要定价的 SKU 行，每行单独成一组。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>SKU Id → 到手价；营销服务不可用时返回空字典。</returns>
    /// <remarks>
    /// <para><b>必须每个 SKU 单独算</b>，不能把它们拍平成一次普通试算。门槛按「适用行金额合计」判：
    /// 「满 100 减 20」遇到一个 200 元与一个 100 元的 SKU，拍平后门槛按合计 300 判过，
    /// 20 元摊到两件上——200 元那件显示 186.67，100 元那件显示 93.33。
    /// 而用户真正下单时只买<b>其中一件</b>，到手价应该是 180 与 80。商品卡上的价低于实付价，
    /// 用户加购结算时发现变贵了，这就是最典型的标价不符投诉。</para>
    ///
    /// <para>「按商品分组」也不对：同一个商品的多个 SKU 之间同样会被摊——
    /// 顾客买的是一件，不是这个商品的全部规格。</para>
    ///
    /// <para>所以这里的分组单位是 <b>单个 SKU</b>。仍然只发一次请求（一屏几十个 SKU 不值得几十次跨服务调用），
    /// 正确性来自「门槛只按单行金额判定」，不来自「查得更多」。</para>
    ///
    /// <para>返回空字典而不是抛异常：营销服务挂了不该让整个商品列表 500。
    /// 调用方此时按**原价**展示——少显示一个优惠，比整页打不开好得多
    /// （BUSINESS.md 11.5 也要求「静默回退原价展示」）。</para>
    /// </remarks>
    Task<IReadOnlyDictionary<long, ShopSkuPrice>> CalculateAsync(
        long customerId, IReadOnlyList<ShopPriceLine> items, CancellationToken ct = default);
}

/// <summary>参与试算的一行。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Amount">SKU 售价。</param>
public readonly record struct ShopPriceLine(long SpuId, long SkuId, decimal Amount);

/// <summary>单个 SKU 的到手价。</summary>
/// <param name="OriginalAmount">原价。</param>
/// <param name="PayableAmount">到手价。</param>
/// <param name="DiscountAmount">优惠合计（活动 + 券）。</param>
/// <param name="Source">优惠来源标签。</param>
/// <param name="SourceName">来源名称（活动名或券码），展示在角标上。</param>
public readonly record struct ShopSkuPrice(
    decimal OriginalAmount,
    decimal PayableAmount,
    decimal DiscountAmount,
    string Source,
    string SourceName);

/// <summary>走内网 HTTP 调用营销服务到手价试算的实现。</summary>
public sealed class HttpShopPriceClient : IShopPriceClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpShopPriceClient> _logger;

    /// <summary>构造客户端。</summary>
    /// <param name="http">指向营销服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpShopPriceClient(HttpClient http, ILogger<HttpShopPriceClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, ShopSkuPrice>> CalculateAsync(
        long customerId, IReadOnlyList<ShopPriceLine> items, CancellationToken ct = default)
    {
        var result = new Dictionary<long, ShopSkuPrice>();
        if (items.Count == 0) return result;

        // 每个 SKU 单独成一组：门槛只按它自己的金额判定。
        // 一次请求算完全部 SKU，不做 N+1 次跨服务调用。
        // Select 得到的就是 Line[][]（每个元素是一「组」，这里每组只有一行），
        // 不要再在外面套一层数组——那会变成 Line[][][]，多出一层没人读的维度。
        var groups = items.Select(a => new[] { new Line(a.SpuId, a.SkuId, a.Amount) }).ToArray();

        var payload = new FinalPriceBatchRequest(customerId, groups, 0, 0);

        try
        {
            var body = await _http
                .PostAsJsonAsync("marketing/activities/FinalPriceBatch", payload, ct)
                .ConfigureAwait(false);

            if (!body.IsSuccessStatusCode)
            {
                _logger.LogError("营销服务到手价试算返回 HTTP {Code}，本次按原价展示", (int)body.StatusCode);
                return result;
            }

            var envelope = await body.Content
                .ReadFromJsonAsync<ApiResponse<List<FinalPriceData>>>(ct).ConfigureAwait(false);

            if (envelope is null || !envelope.Success || envelope.Data is null)
            {
                _logger.LogError("营销服务到手价试算失败：{Message}", envelope?.Message ?? "(空响应)");
                return result;
            }

            foreach (var group in envelope.Data)
            {
                foreach (var line in group.Lines)
                {
                    result[line.SkuId] = new ShopSkuPrice(
                        line.OriginalAmount,
                        line.PayableAmount,
                        line.ActivityDiscount + line.CouponDiscount,
                        line.Source,
                        line.SourceName);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 营销服务不可用时按原价展示，不让整个商品列表挂掉。
            // 这是 BUSINESS.md 11.5 明确要求的「静默回退原价展示」。
            _logger.LogError(ex, "调用营销服务到手价试算失败，本次按原价展示");
        }

        return result;
    }

    /// <summary>分组试算请求体。字段与营销服务的 <c>CalculateFinalPriceBatchCommand</c> 对齐。</summary>
    /// <param name="CustomerId">客户 Id，0 表示游客。</param>
    /// <param name="Groups">分组订单行，每组一个 SKU。</param>
    /// <param name="SessionId">场次 Id，普通场景传 0。</param>
    /// <param name="PlatformId">平台 Id，0 表示不限。</param>
    private sealed record FinalPriceBatchRequest(long CustomerId, Line[][] Groups, long SessionId, long PlatformId);

    /// <summary>订单行。</summary>
    /// <param name="SpuId">SPU Id。</param>
    /// <param name="SkuId">SKU Id。</param>
    /// <param name="Amount">金额。</param>
    private sealed record Line(long SpuId, long SkuId, decimal Amount);

    /// <summary>试算结果数据。</summary>
    /// <param name="Lines">逐行拆分。</param>
    private sealed record FinalPriceData(IReadOnlyList<FinalPriceLine> Lines);

    /// <summary>逐行拆分。</summary>
    /// <param name="SkuId">SKU Id。</param>
    /// <param name="OriginalAmount">原价。</param>
    /// <param name="ActivityDiscount">活动优惠额。</param>
    /// <param name="CouponDiscount">券优惠额。</param>
    /// <param name="PayableAmount">到手价。</param>
    /// <param name="Source">优惠来源标签。</param>
    /// <param name="SourceName">来源名称。</param>
    private sealed record FinalPriceLine(
        [property: JsonPropertyName("skuId")] long SkuId,
        [property: JsonPropertyName("originalAmount")] decimal OriginalAmount,
        [property: JsonPropertyName("activityDiscount")] decimal ActivityDiscount,
        [property: JsonPropertyName("couponDiscount")] decimal CouponDiscount,
        [property: JsonPropertyName("payableAmount")] decimal PayableAmount,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("sourceName")] string SourceName);
}