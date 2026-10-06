using Microsoft.Extensions.Logging;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;
using OrderService.Domain.Services;

namespace OrderService.Application;

/// <summary>把客户端报的行**纠正**成服务端认的行，并顺带算出运费。</summary>
/// <remarks>
/// <para><b>为什么单独抽出来</b>：结算试算与真实下单必须给出<b>同一个金额</b>。
/// 两处各写一遍的话，改了其中一处，另一处会静默停留在旧算法上 ——
/// 症状是「结算页显示 A、点下单收 B」，而代码看上去完全正常。
/// 本类就是那个「只有一份」的地方。</para>
///
/// <para>纠正三件事，全部以服务端数据为准：
/// <b>单价</b>（否则 25.50 的商品能按 0.01 成交）、
/// <b>配送方式</b>（否则谎报快递能凭空多收运费）、
/// 以及<b>运费</b>本身（否则平台运费永远收不到）。</para>
/// </remarks>
public sealed class OrderPricingResolver
{
    private readonly IProductPort _products;
    private readonly IPlatformPort _platforms;
    private readonly ILogger<OrderPricingResolver> _logger;

    /// <summary>构造解析器。</summary>
    /// <param name="products">商品端口，回查权威售价与可售状态。</param>
    /// <param name="platforms">平台端口，读平台级运费配置。</param>
    /// <param name="logger">日志器。</param>
    public OrderPricingResolver(
        IProductPort products, IPlatformPort platforms, ILogger<OrderPricingResolver> logger)
    {
        _products = products;
        _platforms = platforms;
        _logger = logger;
    }

    /// <summary>按权威数据纠正订单行。</summary>
    /// <param name="lines">客户端报的行。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>纠正后的行；失败时返回失败原因。</returns>
    public async Task<ResolveOutcome> ResolveAsync(
        IReadOnlyList<OrderLineRequest> lines, CancellationToken ct)
    {
        var skuIds = lines.Select(a => a.SkuId).Distinct().ToArray();
        var pricing = await _products.GetSkuPricesAsync(skuIds, ct).ConfigureAwait(false);

        var resolved = new List<OrderLineRequest>(lines.Count);
        foreach (var line in lines)
        {
            if (!pricing.TryGetValue(line.SkuId, out var sku))
            {
                _logger.LogError("拒绝下单：SKU {SkuId} 查不到或商品服务不可用", line.SkuId);
                return ResolveOutcome.Fail($"商品 {line.SkuId} 不存在或暂不可售，请刷新后重试");
            }

            if (!sku.Enabled)
            {
                return ResolveOutcome.Fail($"商品规格「{line.SkuId}」已停用");
            }

            if (!sku.SpuApproved)
            {
                return ResolveOutcome.Fail($"商品「{line.SkuId}」尚未通过审核");
            }

            if (!sku.SpuOnShelf)
            {
                return ResolveOutcome.Fail($"商品「{line.SkuId}」已下架");
            }

            if (line.Quantity <= 0)
            {
                return ResolveOutcome.Fail("购买数量必须大于 0");
            }

            // 用权威售价覆盖客户端报的价格。
            // 不因为「报得不一样」就报错 —— 客户端可能拿着旧缓存，直接纠正即可；
            // 只有 SKU 不存在 / 不可售才拒单。
            //
            // ⚠️ 秒杀行例外：它的单价来自场次，不是商品售价，
            // 拿售价覆盖会把秒杀单变成原价单（TEST_CASES API-SEC-002）。
            var isSeckill = line.SourceType == OrderSourceTypes.Seckill;
            resolved.Add(line with
            {
                UnitPrice = isSeckill ? line.UnitPrice : sku.Price,
                DeliveryType = sku.DeliveryType,
            });
        }

        return ResolveOutcome.Ok(resolved);
    }

    /// <summary>按平台配置算出本单该收多少运费（BUSINESS.md 6.2）。</summary>
    /// <param name="platformId">平台 Id。</param>
    /// <param name="lines">已用权威数据纠正过的订单行。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>运费规则；无实物快递行时返回全 0。</returns>
    /// <remarks>
    /// <b>刻意忽略客户端报的运费</b>：它来自客户端，小程序至今直接写 0。
    /// 采信它，后台把平台运费配成 10 元也一分钱收不到。
    /// </remarks>
    public async Task<FreightRule> ResolveFreightAsync(
        long platformId, IReadOnlyList<OrderLineRequest> lines, CancellationToken ct)
    {
        // 没有实物快递行 → 运费恒为 0，不去读平台配置。
        // 虚拟商品与自提都没有物流环节，让它们为一次用不上的跨服务调用买单没有道理。
        if (lines.All(a => a.DeliveryType != DeliveryTypeIds.PhysicalExpress))
        {
            return new FreightRule(0m, 0m);
        }

        var config = await _platforms.GetShippingConfigAsync(platformId, ct).ConfigureAwait(false);

        _logger.LogInformation("运费按平台配置计算：平台 {PlatformId} 运费 {Fee} 包邮门槛 {Threshold}",
            platformId, config.ShippingFee, config.FreeShippingThreshold);

        return new FreightRule(config.ShippingFee, config.FreeShippingThreshold);
    }
}

/// <summary>纠正结果。失败时带上可直接展示给用户的原因。</summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="Lines">纠正后的行。</param>
/// <param name="Error">失败原因。</param>
public readonly record struct ResolveOutcome(
    bool Succeeded, IReadOnlyList<OrderLineRequest> Lines, string Error)
{
    /// <summary>构造一个失败结果。</summary>
    /// <param name="error">失败原因。</param>
    /// <returns>失败结果。</returns>
    public static ResolveOutcome Fail(string error) => new(false, [], error);

    /// <summary>构造一个成功结果。</summary>
    /// <param name="lines">纠正后的行。</param>
    /// <returns>成功结果。</returns>
    public static ResolveOutcome Ok(IReadOnlyList<OrderLineRequest> lines) => new(true, lines, string.Empty);
}

