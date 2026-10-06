using System.Text.Json;

namespace MarketingService.Domain.Services;

/// <summary>参与优惠计算的一行订单项（只保留计算需要的字段）。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Amount">该行金额（单价 × 数量），已含数量。</param>
public readonly record struct CouponOrderLine(long SpuId, long SkuId, decimal Amount);

/// <summary>券在本单上的计算结果。</summary>
/// <param name="CouponId">用户券 Id。</param>
/// <param name="CouponCode">券码。</param>
/// <param name="DiscountAmount">本单实际优惠金额，两位小数。</param>
/// <param name="ThresholdBase">门槛判定基数（适用行金额合计）。</param>
/// <param name="ReachedThreshold">是否达到门槛。</param>
/// <param name="Applicable">是否落在券的适用范围内。</param>
/// <param name="Reason">不可用的原因，便于前端提示「为什么这张券不能用」。</param>
public readonly record struct CouponQuote(
    long CouponId,
    string CouponCode,
    decimal DiscountAmount,
    decimal ThresholdBase,
    bool ReachedThreshold,
    bool Applicable,
    string Reason);

/// <summary>券计算与选用。纯函数，不碰数据库，所以可以单元测试。</summary>
/// <remarks>
/// 规则集中在 BUSINESS.md 11.3~11.4：
/// <list type="bullet">
/// <item><b>门槛基数是「适用行金额合计」</b>，不是订单总额。
/// 一张只对某个 SPU 生效的券，门槛不能用全单金额去判。</item>
/// <item><b>折扣率是数值</b>：8.5 表示 85 折，所以优惠 = 金额 × (10 − 8.5) / 10 = 15%。</item>
/// <item><b>优惠额超过行金额不产生负数</b>：优惠封顶到适用行金额合计。</item>
/// <item><b>满赠折扣额记 0</b>，且只在没有任何折扣可用时才命中。</item>
/// <item>选最优券：<b>优惠额最大</b>，并列时取<b>最临期</b>的那张（BUSINESS.md 11.4）。</item>
/// </list>
/// </remarks>
public static class CouponCalculator
{
    /// <summary>金额舍入：两位小数、四舍五入（ AwayFromZero）。</summary>
    /// <param name="value">原始金额。</param>
    /// <returns>两位小数的金额。</returns>
    /// <remarks>不用默认的银行家舍入：中文语境的「四舍五入」指 0.5 进位，
    /// .NET 默认 ToEven 会把 2.345 舍成 2.34，在金额上会引发对账差异。</remarks>
    public static decimal Round2(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>算一张券在本单上的优惠。</summary>
    /// <param name="coupon">用户券（用快照字段计算，不读模板）。</param>
    /// <param name="lines">订单行。</param>
    /// <returns>计算结果，含不可用原因。</returns>
    public static CouponQuote Quote(Entities.UserCoupon coupon, IReadOnlyList<CouponOrderLine> lines)
    {
        var applicable = ResolveApplicableAmount(coupon, lines);
        var reason = string.Empty;

        if (coupon.CouponType == Entities.CouponTypes.Gift)
        {
            // 满赠不产生折扣额，只有在没有别的折扣可用时才命中。
            // 但**门槛照样要判**：不判的话「满 200 送券」在 50 元的单上也会送出去 ——
            // 运营配的门槛形同虚设，而且症状是「活动太容易中」，很难联想到是这里漏了判断。
            if (!ReachedThreshold(coupon, applicable, out var giftReason))
            {
                return new CouponQuote(coupon.Id, coupon.CouponCode, 0m, applicable, false, true, giftReason);
            }

            return new CouponQuote(coupon.Id, coupon.CouponCode, 0m, applicable, true, true,
                "满赠券，折扣额为 0");
        }

        if (!ReachedThreshold(coupon, applicable, out var thresholdReason))
        {
            return new CouponQuote(coupon.Id, coupon.CouponCode, 0m, applicable, false, true, thresholdReason);
        }

        var discount = coupon.CouponType switch
        {
            Entities.CouponTypes.FullReduction or Entities.CouponTypes.Cash => Round2(coupon.DiscountAmount),
            Entities.CouponTypes.Discount => Round2(applicable * (10m - coupon.DiscountRate) / 10m),
            _ => 0m
        };

        // 优惠额封顶到适用行金额合计，绝不产生负数
        if (discount < 0m) discount = 0m;
        if (discount > applicable) discount = Round2(applicable);

        return new CouponQuote(coupon.Id, coupon.CouponCode, discount, applicable, true, true, reason);
    }

    /// <summary>从用户券里挑「最优券」。</summary>
    /// <param name="candidates">候选券（通常是未使用且未过期的）。</param>
    /// <param name="lines">订单行。</param>
    /// <param name="best">最优券的计算结果；没有可用券时为 null。</param>
    /// <returns>是否选到了券。</returns>
    /// <remarks>
    /// 排序规则（BUSINESS.md 11.4）：优惠额大的优先；**并列时取最临期的**。
    /// 同样金额时先用快过期的，对用户更友好，也避免一堆券同时到期造成核销集中。
    /// </remarks>
    public static bool TryPickBest(
        IEnumerable<Entities.UserCoupon> candidates,
        IReadOnlyList<CouponOrderLine> lines,
        out CouponQuote? best)
    {
        best = null;

        foreach (var coupon in candidates)
        {
            var quote = Quote(coupon, lines);
            if (!quote.ReachedThreshold || quote.DiscountAmount <= 0m) continue;

            if (best is null)
            {
                best = quote;
                continue;
            }

            if (quote.DiscountAmount > best.Value.DiscountAmount)
            {
                best = quote;
            }
            else if (quote.DiscountAmount == best.Value.DiscountAmount)
            {
                // 并列取更临期的
                var a = FindCoupon(candidates, best.Value.CouponId);
                var b = FindCoupon(candidates, quote.CouponId);
                if (a is not null && b is not null && b.ExpireAt < a.ExpireAt)
                {
                    best = quote;
                }
            }
        }

        return best is not null;
    }

    private static Entities.UserCoupon? FindCoupon(IEnumerable<Entities.UserCoupon> all, long id)
        => all.FirstOrDefault(a => a.Id == id);

    /// <summary>算这张券「适用行」的金额合计。</summary>
    /// <param name="coupon">用户券。</param>
    /// <param name="lines">订单行。</param>
    /// <returns>适用行金额合计。</returns>
    public static decimal ResolveApplicableAmount(Entities.UserCoupon coupon, IReadOnlyList<CouponOrderLine> lines)
    {
        if (coupon.TargetType == Entities.TargetTypes.All) return Round2(Sum(lines));

        var targets = ParseTargets(coupon.Targets);
        if (targets.Count == 0) return 0m;

        var hit = coupon.TargetType == Entities.TargetTypes.BySpu
            ? lines.Where(a => targets.Contains(a.SpuId)).Sum(a => a.Amount)
            : lines.Where(a => targets.Contains(a.SkuId)).Sum(a => a.Amount);

        return Round2(hit);
    }

    /// <summary>这张券是否覆盖某个订单行。</summary>
    /// <param name="coupon">用户券。</param>
    /// <param name="line">订单行。</param>
    /// <returns>落在适用范围内返回 true。</returns>
    public static bool CoversLine(Entities.UserCoupon coupon, CouponOrderLine line)
    {
        if (coupon.TargetType == Entities.TargetTypes.All) return true;

        var targets = ParseTargets(coupon.Targets);
        if (targets.Count == 0) return false;

        return coupon.TargetType == Entities.TargetTypes.BySpu
            ? targets.Contains(line.SpuId)
            : targets.Contains(line.SkuId);
    }

    /// <summary>解析 Targets JSON 数组。</summary>
    /// <param name="json">JSON 数组字符串，允许为空或非法。</param>
    /// <returns>Id 集合；解析不出内容时返回空集合而不是抛异常。</returns>
    /// <remarks>
    /// 这里刻意吞掉解析异常返回空集合：Targets 是运营手填的 JSON，
    /// 写错一个字符不该让整个结算页 500。写错的后果是「这张券不适用于任何行」，
    /// 属于可接受的安全降级——券优惠算不出来，但订单金额是对的。
    /// </remarks>
    public static HashSet<long> ParseTargets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new HashSet<long>();
        try
        {
            return JsonSerializer.Deserialize<List<long>>(json) is { } list
                ? new HashSet<long>(list)
                : new HashSet<long>();
        }
        catch (JsonException)
        {
            return new HashSet<long>();
        }
    }

    private static decimal Sum(IReadOnlyList<CouponOrderLine> lines)
    {
        decimal total = 0m;
        foreach (var line in lines) total += line.Amount;
        return total;
    }

    private static bool ReachedThreshold(Entities.UserCoupon coupon, decimal baseAmount, out string reason)
    {
        // 门槛 0 = 无门槛
        if (coupon.ThresholdAmount <= 0m)
        {
            reason = string.Empty;
            return true;
        }

        if (baseAmount >= coupon.ThresholdAmount)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"未达门槛（适用行合计 {baseAmount:0.00}，需满 {coupon.ThresholdAmount:0.00}）";
        return false;
    }
}
