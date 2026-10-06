using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;

namespace MarketingService.Domain.Services;

/// <summary>满赠发放承诺的判定。</summary>
/// <remarks>
/// <para><b>纯函数</b>：只决定「这单该承诺送什么」，不碰数据库，所以可以直接单元测试。
/// 判定必须发生在<b>下单当时</b>：活动时间窗、门槛、赠送张数都按那一刻的口径定下来，
/// 支付时再重算的话，活动一旦被改或过期，用户就会「下单页写着送券、付完钱没有」。</para>
/// </remarks>
public static class GiftGrantPlanner
{
    /// <summary>找出本单真正命中的那个满赠活动。</summary>
    /// <param name="activities">候选活动（查询层已按平台与场次滤过）。</param>
    /// <param name="lines">订单行。</param>
    /// <param name="nowUtc">下单时刻（UTC）。</param>
    /// <param name="giftHit">优惠引擎算出来的结论：本单是否命中了满赠。</param>
    /// <returns>命中的满赠活动；没有则返回 null。</returns>
    /// <remarks>
    /// <b>为什么以优惠引擎的结论为准</b>：满赠只在「该行没有任何折扣可用」时才命中
    /// （BUSINESS.md 11.2），这件事只有算过整单优惠的人知道。
    /// 这里再用同一份 <see cref="PromotionCalculator.PickBest"/> 把活动实体找回来——
    /// 判定规则只有一份，不会出现「引擎说命中、发券说没命中」。
    /// </remarks>
    public static PromotionActivity? FindHitGiftActivity(
        IReadOnlyList<PromotionActivity> activities,
        IReadOnlyList<PromotionLine> lines,
        DateTime nowUtc,
        bool giftHit)
    {
        if (!giftHit || activities.Count == 0 || lines.Count == 0) return null;

        var quote = PromotionCalculator.PickBest(activities, lines, nowUtc);
        if (quote.ActivityId == 0 || !quote.IsGift) return null;

        return activities.FirstOrDefault(a => a.Id == quote.ActivityId);
    }

    /// <summary>这张赠送模板还能不能再承诺一次。</summary>
    /// <param name="template">赠送的券模板；查不到（已删除）时为 null。</param>
    /// <param name="quantity">本次要送的张数。</param>
    /// <returns>还能承诺返回 true。</returns>
    /// <remarks>
    /// 池子在<b>承诺时</b>判：券发不出去就不该向用户承诺。
    /// <c>TotalQuantity = 0</c> 表示不限量（DATA_SPEC 5.12）。
    /// 模板被停用不拦——停用的语义是「不能再领」，而下单时它还是启用的，
    /// 承诺已经作出，付完钱就该兑现。
    /// </remarks>
    public static bool CanPromise(CouponTemplate? template, int quantity)
    {
        if (template is null || quantity <= 0) return false;
        if (template.TotalQuantity <= 0) return true;

        return template.TotalQuantity - template.IssuedQuantity >= quantity;
    }
}
