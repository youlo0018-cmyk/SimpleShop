using MarketingService.Application.Services;
using MarketingService.Domain.Entities;

namespace MarketingService.Application.Features.Promotion;

/// <summary>
/// 解析订单行所属的商户，供「商户级活动只作用于本商户」这条规则使用。
/// </summary>
/// <remarks>
/// <para><b>为什么必须解析</b>：活动分平台级与商户级。C 端试算（FinalPrice）只传
/// SPU / SKU，没有商户字段；不解析的话，一条商户级活动会减到同平台**其它商户**的商品上。</para>
///
/// <para><b>没有商户级活动时不查</b>：FinalPrice 是全系统调用频次最高的接口，
/// 而绝大多数平台只配平台级活动。先看候选活动里有没有 <c>MerchantId &gt; 0</c>，
/// 没有就直接返回空表，不为一个不会用到的维度付一次跨服务调用。</para>
///
/// <para>查不到归属的行按 0（未知）处理：只有平台级活动能命中它 —— 宁可少给优惠。</para>
/// </remarks>
internal static class LineMerchantResolver
{
    /// <summary>解析行商户。</summary>
    /// <param name="activities">本次试算的候选活动。</param>
    /// <param name="skuIds">所有行的 SKU Id。</param>
    /// <param name="products">商品服务端口。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>SKU Id → 商户 Id；不需要或查不到时为空表。</returns>
    public static async Task<IReadOnlyDictionary<long, long>> ResolveAsync(
        IReadOnlyList<PromotionActivity> activities,
        IEnumerable<long> skuIds,
        IProductPort products,
        CancellationToken ct)
    {
        if (!activities.Any(a => a.MerchantId > 0))
        {
            return new Dictionary<long, long>();
        }

        var ids = skuIds.Where(a => a > 0).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<long, long>();

        return await products.GetSkuMerchantsAsync(ids, ct).ConfigureAwait(false);
    }
}
