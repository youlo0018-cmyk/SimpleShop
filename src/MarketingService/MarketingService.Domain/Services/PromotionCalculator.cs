using System.Text.Json;
using MarketingService.Domain.Entities;

namespace MarketingService.Domain.Services;

/// <summary>参与优惠计算的一行（只保留计算需要的字段）。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Amount">该行金额（单价 × 数量），已含数量。</param>
/// <param name="MerchantId">
/// 该行所属商户，0 表示平台自营 / 未知。
/// </param>
/// <remarks>
/// <b>为什么行上必须有商户</b>：活动分平台级与商户级。不带商户维度的话，
/// 一条商户级的活动会减到同平台**其它商户**的商品上 —— 跨商户改价。
/// 判据是 <c>activity.MerchantId &lt;= 0 || activity.MerchantId == line.MerchantId</c>：
/// 平台级活动（0）作用于所有行，商户级只作用于自己家的行。
/// </remarks>
public readonly record struct PromotionLine(long SpuId, long SkuId, decimal Amount, long MerchantId = 0);

/// <summary>一个活动对一行订单的试算结果。</summary>
/// <param name="ActivityId">命中的活动 Id，0 表示没命中。</param>
/// <param name="ActivityName">活动名，用于前端展示「满减」标签。</param>
/// <param name="DiscountAmount">优惠金额，两位小数。</param>
/// <param name="ThresholdBase">门槛判定基数（该活动的适用行金额合计）。</param>
/// <param name="ReachedThreshold">是否达到门槛。</param>
/// <param name="Applicable">该行是否落在活动适用范围内。</param>
/// <param name="IsGift">是否满赠（折扣额记 0）。</param>
/// <param name="Reason">未命中的原因，便于「为什么没享受活动」的提示。</param>
public readonly record struct ActivityQuote(
    long ActivityId,
    string ActivityName,
    decimal DiscountAmount,
    decimal ThresholdBase,
    bool ReachedThreshold,
    bool Applicable,
    bool IsGift,
    string Reason);

/// <summary>一行的最终优惠结果。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="OriginalAmount">原价。</param>
/// <param name="ActivityDiscount">活动优惠额。</param>
/// <param name="CouponDiscount">分摊到该行的券优惠额。</param>
/// <param name="PayableAmount">到手价，<b>封底 0.01</b>。</param>
/// <param name="Source">优惠来源标签，见 <see cref="DiscountSources"/>。</param>
/// <param name="SourceName">来源名称（活动名或券码），用于角标文案。</param>
/// <param name="IsFloored">是否被 0.01 封底抬起来了。</param>
public readonly record struct PromotionLineResult(
    long SpuId,
    long SkuId,
    decimal OriginalAmount,
    decimal ActivityDiscount,
    decimal CouponDiscount,
    decimal PayableAmount,
    string Source,
    string SourceName,
    bool IsFloored);

/// <summary>整单到手价试算结果。</summary>
/// <param name="Lines">逐行结果。</param>
/// <param name="OriginalTotal">原价合计。</param>
/// <param name="ActivityDiscountTotal">活动优惠合计。</param>
/// <param name="CouponDiscountTotal">券优惠合计。</param>
/// <param name="FinalPrice">到手价合计。<b>不含运费</b>（BUSINESS.md 11.5）。</param>
/// <param name="CouponId">实际选中的券 Id，0 表示没用券。</param>
/// <param name="CouponCode">实际选中的券码。</param>
/// <param name="UsedActivity">是否命中了活动。</param>
public readonly record struct FinalPriceResult(
    IReadOnlyList<PromotionLineResult> Lines,
    decimal OriginalTotal,
    decimal ActivityDiscountTotal,
    decimal CouponDiscountTotal,
    decimal FinalPrice,
    long CouponId,
    string CouponCode,
    bool UsedActivity);

/// <summary>活动计算。纯函数，不碰数据库，所以可以单元测试。</summary>
/// <remarks>
/// 规则集中在 BUSINESS.md 11.2 / 11.3，逐条对应下面的注释：
/// <list type="number">
/// <item><b>贪心 + 逐行独立</b>：每行单独算自己那份，互不影响。</item>
/// <item><b>多活动冲突</b>：优惠力度最大 → 门槛最高 → 创建最早，保证结果确定。</item>
/// <item><b>单行封底 0.01</b>：券与活动不能把某一行打成 0 元。</item>
/// <item><b>满赠折扣额记 0</b>，且只在没有任何折扣可用时才命中。</item>
/// <item><b>活动与券互斥</b>：落在券作用域内的行不再命中活动；作用域外的行照常参与活动。</item>
/// <item><b>平台优先级</b>：1 活动优先 / 2 券优先（默认）。</item>
/// </list>
/// </remarks>
public static class PromotionCalculator
{
    /// <summary>单行封底金额。券与活动都打不到 0。</summary>
    public const decimal LineFloor = 0.01m;

    /// <summary>金额舍入：两位小数、四舍五入（AwayFromZero）。</summary>
    /// <param name="value">原始金额。</param>
    /// <returns>两位小数的金额。</returns>
    public static decimal Round2(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>算一个活动对一行订单的优惠。</summary>
    /// <param name="activity">活动。</param>
    /// <param name="lines">整单的行（门槛基数是<b>适用行金额合计</b>）。</param>
    /// <param name="nowUtc">当前时间，用于过滤时间窗。</param>
    /// <returns>试算结果。</returns>
    public static ActivityQuote Quote(PromotionActivity activity, IReadOnlyList<PromotionLine> lines, DateTime nowUtc)
    {
        var applicable = ResolveApplicableAmount(activity, lines);

        // 时间窗与状态<b>不过滤也不参与计算</b>：查询层已经滤过一次，
        // 这里再滤一次是为了让纯函数单独测试时行为确定（不能依赖调用方先滤过）
        if (activity.Status != 1)
        {
            return Miss(activity, applicable, "活动已停用");
        }

        if (nowUtc < activity.StartTime || nowUtc > activity.EndTime)
        {
            return Miss(activity, applicable, "不在活动时间内");
        }

        if (!IsLineInScope(activity, lines))
        {
            return new ActivityQuote(activity.Id, activity.ActivityName, 0m, applicable, false, false, false, "该商品不在活动范围内");
        }

        if (activity.ActivityType == ActivityTypes.Gift)
        {
            // 满赠折扣额是 0，但门槛必须达到——达门槛送券、没达到什么都不送
            return applicable < activity.ThresholdAmount
                ? Miss(activity, applicable, $"未满 {activity.ThresholdAmount:0.00} 元")
                : new ActivityQuote(activity.Id, activity.ActivityName, 0m, applicable, true, true, true, "满赠活动");
        }

        if (applicable < activity.ThresholdAmount)
        {
            return Miss(activity, applicable, $"未满 {activity.ThresholdAmount:0.00} 元");
        }

        var discount = activity.ActivityType switch
        {
            ActivityTypes.FullReduction => Round2(activity.DiscountAmount),
            ActivityTypes.Discount => Round2(applicable * (10m - activity.DiscountRate) / 10m),
            _ => 0m
        };

        if (discount < 0m) discount = 0m;
        if (discount > applicable) discount = Round2(applicable);

        return new ActivityQuote(activity.Id, activity.ActivityName, discount, applicable, true, true, false, string.Empty);
    }

    /// <summary>从活动里挑这一行能命中的<b>最优</b>那一个。</summary>
    /// <param name="activities">候选活动（通常已按时间窗与状态滤过）。</param>
    /// <param name="lines">整单的行。</param>
    /// <param name="nowUtc">当前时间。</param>
    /// <returns>最优活动的试算结果；一个都没命中时 ActivityId 为 0。</returns>
    /// <remarks>
    /// 比较顺序（BUSINESS.md 11.2）：优惠力度最大 → 门槛最高 → 创建最早。
    /// 三级比较都是必需的：只看优惠额的话，两个活动给一样的钱，
    /// 选哪个就要看创建时间——结果必须是确定的，否则同一单两次试算给出不同优惠，
    /// 用户会来投诉「为什么价格还会变」。
    /// </remarks>
    public static ActivityQuote PickBest(
        IReadOnlyList<PromotionActivity> activities, IReadOnlyList<PromotionLine> lines, DateTime nowUtc)
    {
        ActivityQuote? best = null;
        PromotionActivity? bestEntity = null;

        foreach (var activity in activities)
        {
            var quote = Quote(activity, lines, nowUtc);
            if (!quote.Applicable || !quote.ReachedThreshold) continue;

            if (best is null)
            {
                best = quote;
                bestEntity = activity;
                continue;
            }

            // 满赠（折扣 0）只在没有任何折扣可用时才命中，所以有正数优惠时一定压过满赠
            if (quote.DiscountAmount > best.Value.DiscountAmount)
            {
                best = quote;
                bestEntity = activity;
                continue;
            }

            if (quote.DiscountAmount == best.Value.DiscountAmount)
            {
                // 力度相同取门槛更高
                if (activity.ThresholdAmount > bestEntity!.ThresholdAmount)
                {
                    best = quote;
                    bestEntity = activity;
                }
                else if (activity.ThresholdAmount == bestEntity!.ThresholdAmount
                         && activity.CreatedAt < bestEntity.CreatedAt)
                {
                    // 门槛也相同取创建最早
                    best = quote;
                    bestEntity = activity;
                }
            }
        }

        return best ?? default;
    }

    /// <summary>整单到手价试算。</summary>
    /// <param name="lines">订单行。</param>
    /// <param name="activities">候选活动。</param>
    /// <param name="coupons">客户可用券；游客传空集合。</param>
    /// <param name="priority">平台优惠优先级，见 <see cref="MarketingPriorities"/>。</param>
    /// <param name="nowUtc">当前时间。</param>
    /// <returns>整单结果，含逐行拆分。</returns>
    /// <remarks>
    /// <para><b>券与活动都是「整单一个优惠额」，不是「每行各减一次」。</b>
    /// 「满 100 减 20」遇到三行各 50 元，正确结果是三行各减 6.67 而不是各减 20——
    /// 后者会让优惠额变成 60 元，直接把商家的钱减穿。这一条是整个计算里最容易写错的地方。
    /// 所以流程是：先在订单级选出最优来源，再把它的优惠额<b>按行金额比例分摊</b>回各行。</para>
    ///
    /// <para><b>互斥的实现</b>：用券就把落在券作用域内的行标记为「已被券覆盖」，
    /// 这些行不再参与活动评选；作用域外的行照常参与活动（BUSINESS.md 11.3）。</para>
    ///
    /// <para><b>游客</b>传空券集合，逻辑与「没有可用券」完全一致，只算活动价——
    /// 这正是 11.5 要求的「游客只计算活动价，不计券」。</para>
    /// </remarks>
    public static FinalPriceResult Calculate(
        IReadOnlyList<PromotionLine> lines,
        IReadOnlyList<PromotionActivity> activities,
        IReadOnlyList<UserCoupon> coupons,
        int priority,
        DateTime nowUtc)
    {
        var originals = lines.Select(a => Round2(a.Amount)).ToArray();
        var originalTotal = Round2(originals.Sum());

        // ---- ① 订单级选券（游客传空集合，这里自然选不中） ----
        var couponLines = lines.Select(a => new CouponOrderLine(a.SpuId, a.SkuId, a.Amount)).ToArray();
        CouponQuote? couponQuote = null;

        if (coupons.Count > 0 && CouponCalculator.TryPickBest(coupons, couponLines, out var picked) && picked is not null)
        {
            couponQuote = picked;
        }

        // ---- ② 订单级选活动 ----
        var activityQuote = PickBest(activities, lines, nowUtc);
        var hasActivity = activityQuote.ActivityId != 0 && activityQuote.ReachedThreshold;
        var activityScope = hasActivity
            ? ResolveScopeSkus(activities, activityQuote.ActivityId, lines)
            : new HashSet<long>();

        var couponScope = couponQuote is null
            ? new HashSet<long>()
            : ResolveCouponScopeSkus(coupons, couponQuote.Value.CouponId, lines);

        // ---- ③ 按平台优先级二选一 ----
        // 券优先（默认）：先取最优券，无券才取活动。
        // 活动优先：先取活动，无活动才取券。
        var couponFirst = priority != MarketingPriorities.ActivityFirst;
        var useCoupon = couponQuote is not null && (couponFirst || !hasActivity);
        var useActivity = !useCoupon && hasActivity;

        // ---- ④ 把选中的优惠额分摊回它覆盖到的那些行 ----
        var results = new List<PromotionLineResult>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            results.Add(new PromotionLineResult(
                lines[i].SpuId, lines[i].SkuId, originals[i], 0m, 0m, originals[i],
                DiscountSources.None, string.Empty, false));
        }

        if (useActivity)
        {
            var total = Math.Min(activityQuote.DiscountAmount, SumOf(results, activityScope));
            ApplyDiscount(results, activityScope, total, DiscountSources.Activity, activityQuote.ActivityName);
        }
        else if (useCoupon && couponQuote is not null)
        {
            var total = Math.Min(couponQuote.Value.DiscountAmount, SumOf(results, couponScope));
            ApplyDiscount(results, couponScope, total, DiscountSources.Coupon, couponQuote.Value.CouponCode);
        }

        // 满赠的优惠额是 0，上面的分摊会直接跳过——但它**确实命中了**，
        // 商品卡上要显示「满 100 赠券」而不是「无优惠」，所以单独把角标打上
        if (useActivity && activityQuote.IsGift)
        {
            for (var i = 0; i < results.Count; i++)
            {
                if (!activityScope.Contains(results[i].SkuId)) continue;
                results[i] = results[i] with { Source = DiscountSources.Gift, SourceName = activityQuote.ActivityName };
            }
        }

        // ---- ⑤ 逐行算到手价并封底 ----
        for (var i = 0; i < results.Count; i++)
        {
            var a = results[i];
            var payable = Round2(a.OriginalAmount - a.ActivityDiscount - a.CouponDiscount);
            var floored = false;

            if (payable < LineFloor)
            {
                payable = LineFloor;
                floored = true;
            }

            results[i] = a with { PayableAmount = payable, IsFloored = floored };
        }

        var activityTotal = Round2(results.Sum(a => a.ActivityDiscount));
        var couponTotal = Round2(results.Sum(a => a.CouponDiscount));

        return new FinalPriceResult(
            results,
            originalTotal,
            activityTotal,
            couponTotal,
            Round2(results.Sum(a => a.PayableAmount)),
            useCoupon ? couponQuote!.Value.CouponId : 0,
            useCoupon ? couponQuote!.Value.CouponCode : string.Empty,
            useActivity && results.Any(a => a.Source is DiscountSources.Activity or DiscountSources.Gift));
    }

    /// <summary>把一个整单优惠额按行金额比例分摊到它覆盖到的行上。</summary>
    /// <param name="results">逐行结果，原地写入折扣额与来源。</param>
    /// <param name="scope">被这个优惠覆盖到的 SKU 集合。</param>
    /// <param name="total">要分摊的优惠总额。</param>
    /// <param name="source">来源标签，见 <see cref="DiscountSources"/>。</param>
    /// <param name="sourceName">来源名称（活动名或券码），展示在优惠角标上。</param>
    private static void ApplyDiscount(
        List<PromotionLineResult> results, HashSet<long> scope, decimal total, string source, string sourceName)
    {
        var indexes = new List<int>();
        for (var i = 0; i < results.Count; i++)
        {
            if (scope.Contains(results[i].SkuId)) indexes.Add(i);
        }

        if (indexes.Count == 0 || total <= 0m) return;

        var baseSum = Round2(indexes.Sum(a => results[a].OriginalAmount));
        if (baseSum <= 0m) return;

        // 余数全给金额最大的那一行：按比例逐行 Round 一定会丢掉几分钱，
        // 结果就是「各行优惠之和 ≠ 整单优惠」，对账时又是一次「差一分钱」
        var largest = indexes[0];
        for (var k = 1; k < indexes.Count; k++)
        {
            if (results[indexes[k]].OriginalAmount > results[largest].OriginalAmount) largest = indexes[k];
        }

        var allocated = 0m;
        foreach (var i in indexes)
        {
            if (i == largest) continue;

            var part = Round2(total * results[i].OriginalAmount / baseSum);
            results[i] = WithDiscount(results[i], part, source, sourceName);
            allocated += part;
        }

        var last = Round2(total - allocated);
        results[largest] = WithDiscount(results[largest], last, source, sourceName);
    }

    /// <summary>给一行写入折扣额与来源标签。</summary>
    /// <param name="line">该行当前结果。</param>
    /// <param name="discount">该行分摊到的优惠额。</param>
    /// <param name="source">来源标签。</param>
    /// <param name="sourceName">来源名称。</param>
    /// <returns>写入后的行结果。</returns>
    private static PromotionLineResult WithDiscount(
        PromotionLineResult line, decimal discount, string source, string sourceName)
        => source == DiscountSources.Coupon
            ? line with { CouponDiscount = discount, Source = source, SourceName = sourceName }
            : line with { ActivityDiscount = discount, Source = source, SourceName = sourceName };

    /// <summary>求指定 SKU 集合内的原价合计。</summary>
    /// <param name="results">逐行结果。</param>
    /// <param name="scope">SKU 集合。</param>
    /// <returns>合计，两位小数。</returns>
    private static decimal SumOf(List<PromotionLineResult> results, HashSet<long> scope)
        => Round2(results.Where(a => scope.Contains(a.SkuId)).Sum(a => a.OriginalAmount));

    /// <summary>算出某个活动在订单里覆盖到的 SKU 集合。</summary>
    /// <param name="activities">候选活动。</param>
    /// <param name="activityId">活动 Id。</param>
    /// <param name="lines">订单行。</param>
    /// <returns>被覆盖的 SKU Id 集合。</returns>
    private static HashSet<long> ResolveScopeSkus(
        IReadOnlyList<PromotionActivity> activities, long activityId,
        IReadOnlyList<PromotionLine> lines)
    {
        var result = new HashSet<long>();
        var activity = activities.FirstOrDefault(a => a.Id == activityId);

        if (activity is null)
        {
            foreach (var l in lines) result.Add(l.SkuId);
            return result;
        }

        // 商户级活动只覆盖自己家的行：不判这一条，优惠额会分摊到别的商户的商品上
        var own = lines.Where(l => CoversMerchant(activity, l)).ToList();

        if (activity.TargetType == TargetTypes.All)
        {
            foreach (var l in own) result.Add(l.SkuId);
            return result;
        }

        var ids = ParseTargets(activity.Targets);
        foreach (var l in own)
        {
            if (activity.TargetType == TargetTypes.BySpu ? ids.Contains(l.SpuId) : ids.Contains(l.SkuId))
            {
                result.Add(l.SkuId);
            }
        }

        return result;
    }

    /// <summary>算活动「适用行金额合计」。</summary>
    /// <param name="activity">活动。</param>
    /// <param name="lines">订单行。</param>
    /// <returns>适用行金额合计。</returns>
    public static decimal ResolveApplicableAmount(PromotionActivity activity, IReadOnlyList<PromotionLine> lines)
    {
        // 商户维度先过一遍：不属于本活动的行不计入门槛基数
        var own = lines.Where(a => CoversMerchant(activity, a)).ToList();

        if (activity.TargetType == TargetTypes.All) return Round2(own.Sum(a => Round2(a.Amount)));

        var ids = ParseTargets(activity.Targets);

        return Round2(own
            .Where(a => activity.TargetType == TargetTypes.BySpu ? ids.Contains(a.SpuId) : ids.Contains(a.SkuId))
            .Sum(a => Round2(a.Amount)));
    }

    /// <summary>活动是否至少覆盖到一行。</summary>
    /// <param name="activity">活动。</param>
    /// <param name="lines">订单行。</param>
    /// <returns>覆盖到任意一行返回 true。</returns>
    public static bool IsLineInScope(PromotionActivity activity, IReadOnlyList<PromotionLine> lines)
    {
        var own = lines.Where(a => CoversMerchant(activity, a)).ToList();

        if (activity.TargetType == TargetTypes.All) return own.Count > 0;

        var ids = ParseTargets(activity.Targets);
        return own.Any(a => activity.TargetType == TargetTypes.BySpu ? ids.Contains(a.SpuId) : ids.Contains(a.SkuId));
    }

    /// <summary>判断活动是否覆盖该订单行的商户。</summary>
    /// <param name="activity">活动。</param>
    /// <param name="line">订单行。</param>
    /// <returns>覆盖返回 true。</returns>
    /// <remarks>
    /// 平台级活动（<c>MerchantId = 0</c>）作用于所有行；商户级只作用于本商户的行。
    /// 行上的商户为 0（未知）时只有平台级活动能命中 —— 宁可少给优惠，
    /// 也不能把一条商户级活动算到身份不明的行上。
    /// </remarks>
    public static bool CoversMerchant(PromotionActivity activity, PromotionLine line)
        => activity.MerchantId <= 0 || activity.MerchantId == line.MerchantId;

    /// <summary>解析适用范围的 JSON。</summary>
    /// <param name="targets">JSON 文本，解析失败按空集合处理。</param>
    /// <returns>Id 集合。</returns>
    /// <remarks>
    /// 解析失败返回空集合而不是抛异常：适用范围写坏的后果应该是
    /// 「这个活动对谁都��生效」，而不是整个下单页 500。
    /// </remarks>
    public static HashSet<long> ParseTargets(string? targets)
    {
        if (string.IsNullOrWhiteSpace(targets)) return new HashSet<long>();

        try
        {
            return JsonSerializer.Deserialize<long[]>(targets)?.ToHashSet() ?? new HashSet<long>();
        }
        catch (JsonException)
        {
            return new HashSet<long>();
        }
    }

    /// <summary>算出这张券在订单里覆盖到的 SKU 集合。</summary>
    /// <param name="coupons">全部候选券。</param>
    /// <param name="couponId">选中的券 Id。</param>
    /// <param name="lines">订单行。</param>
    /// <returns>被覆盖的 SKU Id 集合。</returns>
    private static HashSet<long> ResolveCouponScopeSkus(
        IReadOnlyList<UserCoupon> coupons, long couponId, IReadOnlyList<PromotionLine> lines)
    {
        var result = new HashSet<long>();

        var coupon = coupons.FirstOrDefault(a => a.Id == couponId);
        if (coupon is null || coupon.TargetType == TargetTypes.All)
        {
            foreach (var l in lines) result.Add(l.SkuId);
            return result;
        }

        var ids = ParseTargets(coupon.Targets);
        foreach (var l in lines)
        {
            if (coupon.TargetType == TargetTypes.BySpu ? ids.Contains(l.SpuId) : ids.Contains(l.SkuId))
            {
                result.Add(l.SkuId);
            }
        }

        return result;
    }

    private static ActivityQuote Miss(PromotionActivity activity, decimal baseAmount, string reason)
        => new(activity.Id, activity.ActivityName, 0m, baseAmount, false, true, false, reason);
}
