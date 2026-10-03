using MarketingService.Domain.Entities;
using MarketingService.Domain.Services;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>营销活动计算引擎的单元测试。</summary>
/// <remarks>
/// 这组用例保护的是**钱**。规则来自 BUSINESS.md 11.2 / 11.3，逐条对应：
/// 贪心逐行、多活动冲突的三级比较、单行封底 0.01、满赠折扣额记 0、
/// 活动与券互斥、平台优先级二选一。
///
/// 最容易写错也最伤钱的一条是「整单优惠额要分摊」：
/// 满 100 减 20 遇到三行各 50 元，正确是各减 6.67，而不是各减 20——
/// 后者会让优惠额变成 60 元，直接把商家的钱减穿。所以 UT-PRM-002 专门盯这个。
/// </remarks>
public class PromotionCalculatorTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static PromotionActivity Activity(
        long id, int type = ActivityTypes.FullReduction,
        decimal threshold = 0m, decimal amount = 0m, decimal rate = 0m,
        int targetType = TargetTypes.All, string targets = "[]",
        DateTime? start = null, DateTime? end = null,
        DateTime? createdAt = null, int status = 1, long platformId = 0)
        => new()
        {
            Id = id,
            PlatformId = platformId,
            ActivityName = $"活动{id}",
            ActivityType = type,
            ThresholdAmount = threshold,
            DiscountAmount = amount,
            DiscountRate = rate,
            TargetType = targetType,
            Targets = targets,
            StartTime = start ?? Now.AddDays(-1),
            EndTime = end ?? Now.AddDays(1),
            CreatedAt = createdAt ?? Now.AddDays(-2),
            Status = status
        };

    private static PromotionLine Line(long spu, long sku, decimal amount)
        => new(spu, sku, amount);

    [Fact]
    public void 满减命中时优惠额等于配置值()
    {
        var activity = Activity(1, ActivityTypes.FullReduction, threshold: 100m, amount: 20m);
        var lines = new[] { Line(100, 1001, 50m), Line(100, 1002, 50m), Line(100, 1003, 50m) };

        var quote = PromotionCalculator.Quote(activity, lines, Now);

        Assert.True(quote.Applicable);
        Assert.True(quote.ReachedThreshold);
        Assert.Equal(20m, quote.DiscountAmount);
        Assert.Equal(150m, quote.ThresholdBase);
    }

    [Fact]
    public void 门槛基数是适用行金额合计而不是订单总额()
    {
        // 只对 SKU 1001 生效，门槛 40。它只在这一行（50 元）上判门槛，
        // 不能拿全单 150 元去判——否则一个只值 10 元的商品也能凑够门槛。
        var activity = Activity(1, ActivityTypes.FullReduction, threshold: 40m, amount: 20m,
            targetType: TargetTypes.BySku, targets: "[1001]");

        var lines = new[] { Line(100, 1001, 50m), Line(200, 2001, 5m) };

        var quote = PromotionCalculator.Quote(activity, lines, Now);

        Assert.Equal(50m, quote.ThresholdBase);
        Assert.True(quote.ReachedThreshold);
    }

    [Fact]
    public void 未达门槛不命中且给出原因()
    {
        var activity = Activity(1, ActivityTypes.FullReduction, threshold: 100m, amount: 20m);
        var quote = PromotionCalculator.Quote(activity, new[] { Line(100, 1001, 30m) }, Now);

        Assert.False(quote.ReachedThreshold);
        Assert.Equal(0m, quote.DiscountAmount);
        Assert.Contains("未满", quote.Reason);
    }

    [Fact]
    public void 满折按折扣率算优惠()
    {
        // 8.5 折 = 优惠 15%
        var activity = Activity(1, ActivityTypes.Discount, threshold: 100m, rate: 8.5m);
        var quote = PromotionCalculator.Quote(activity, new[] { Line(100, 1001, 100m) }, Now);

        Assert.Equal(15m, quote.DiscountAmount);
    }

    [Fact]
    public void 满赠折扣额记0但算命中()
    {
        var activity = Activity(1, ActivityTypes.Gift, threshold: 100m, amount: 0m);
        var quote = PromotionCalculator.Quote(activity, new[] { Line(100, 1001, 100m) }, Now);

        Assert.True(quote.IsGift);
        Assert.True(quote.ReachedThreshold);
        Assert.Equal(0m, quote.DiscountAmount);
    }

    [Fact]
    public void 满赠在有折扣活动可用时让位()
    {
        // 满赠 0 元优惠，满减 20 元 → 选满减
        var gift = Activity(1, ActivityTypes.Gift, threshold: 50m);
        var reduction = Activity(2, ActivityTypes.FullReduction, threshold: 50m, amount: 20m);

        var best = PromotionCalculator.PickBest(
            new[] { gift, reduction }, new[] { Line(100, 1001, 100m) }, Now);

        Assert.Equal(2, best.ActivityId);
        Assert.Equal(20m, best.DiscountAmount);
    }

    [Fact]
    public void 满赠在没有折扣可用时命中()
    {
        var gift = Activity(1, ActivityTypes.Gift, threshold: 50m);
        var best = PromotionCalculator.PickBest(new[] { gift }, new[] { Line(100, 1001, 100m) }, Now);

        Assert.Equal(1, best.ActivityId);
        Assert.True(best.IsGift);
        Assert.Equal(0m, best.DiscountAmount);
    }

    [Fact]
    public void 多活动冲突取优惠力度最大的()
    {
        var small = Activity(1, ActivityTypes.FullReduction, threshold: 50m, amount: 5m);
        var big = Activity(2, ActivityTypes.FullReduction, threshold: 80m, amount: 20m);

        var best = PromotionCalculator.PickBest(
            new[] { small, big }, new[] { Line(100, 1001, 100m) }, Now);

        Assert.Equal(2, best.ActivityId);
    }

    [Fact]
    public void 优惠相同取门槛更高的()
    {
        var low = Activity(1, ActivityTypes.FullReduction, threshold: 50m, amount: 20m);
        var high = Activity(2, ActivityTypes.FullReduction, threshold: 90m, amount: 20m);

        var best = PromotionCalculator.PickBest(
            new[] { low, high }, new[] { Line(100, 1001, 100m) }, Now);

        Assert.Equal(2, best.ActivityId);
    }

    [Fact]
    public void 优惠与门槛都相同时取创建最早的()
    {
        // 第三级比较是为了让结果**确定**：同一单两次试算必须给出同一个活动，
        // 否则用户会问「为什么价格还会变」，而那时没人答得上来。
        var later = Activity(1, ActivityTypes.FullReduction, threshold: 50m, amount: 20m,
            createdAt: Now.AddDays(-1));
        var earlier = Activity(2, ActivityTypes.FullReduction, threshold: 50m, amount: 20m,
            createdAt: Now.AddDays(-5));

        var best = PromotionCalculator.PickBest(
            new[] { later, earlier }, new[] { Line(100, 1001, 100m) }, Now);

        Assert.Equal(2, best.ActivityId);
    }

    [Fact]
    public void 时间窗外的活动不命中()
    {
        var expired = Activity(1, ActivityTypes.FullReduction, threshold: 0m, amount: 20m,
            start: Now.AddDays(-10), end: Now.AddDays(-5));

        var quote = PromotionCalculator.Quote(expired, new[] { Line(100, 1001, 100m) }, Now);

        Assert.False(quote.ReachedThreshold);
        Assert.Contains("不在活动时间内", quote.Reason);
    }

    [Fact]
    public void 停用的活动不命中()
    {
        var stopped = Activity(1, ActivityTypes.FullReduction, threshold: 0m, amount: 20m, status: 2);
        var quote = PromotionCalculator.Quote(stopped, new[] { Line(100, 1001, 100m) }, Now);

        Assert.False(quote.ReachedThreshold);
        Assert.Contains("已停用", quote.Reason);
    }

    [Fact]
    public void 适用范围外的行活动不适用()
    {
        var activity = Activity(1, ActivityTypes.FullReduction, threshold: 0m, amount: 20m,
            targetType: TargetTypes.BySku, targets: "[9999]");

        var quote = PromotionCalculator.Quote(activity, new[] { Line(100, 1001, 100m) }, Now);

        Assert.False(quote.Applicable);
    }

    // ---------------- 整单到手价 ----------------

    [Fact]
    public void 整单优惠额必须分摊到各行而不是每行各减一次()
    {
        // 三行各 50，满 100 减 20 → 各行约减 6.67，合计正好 20.00。
        // 若按「每行各减 20」实现，优惠额会变成 60 元——把商家的钱减穿。
        var activity = Activity(1, ActivityTypes.FullReduction, threshold: 100m, amount: 20m);
        var lines = new[] { Line(100, 1001, 50m), Line(100, 1002, 50m), Line(100, 1003, 50m) };

        var result = PromotionCalculator.Calculate(
            lines, new[] { activity }, Array.Empty<UserCoupon>(), MarketingPriorities.CouponFirst, Now);

        Assert.Equal(150m, result.OriginalTotal);
        Assert.Equal(20m, result.ActivityDiscountTotal);
        Assert.Equal(130m, result.FinalPrice);

        // 各行优惠之和必须等于整单优惠，否则对账会差一分钱
        Assert.Equal(20m, result.Lines.Sum(a => a.ActivityDiscount));

        // 余数给金额最大的那一行；这里三行金额相同，取第一条
        Assert.Equal(6.66m, result.Lines[0].ActivityDiscount);
        Assert.Equal(6.67m, result.Lines[1].ActivityDiscount);
        Assert.Equal(6.67m, result.Lines[2].ActivityDiscount);
    }

    [Fact]
    public void 单行封底到001而不是0()
    {
        // 满减 100 把 100.00 打到 0；行应付必须被抬到 0.01，不能是 0 元
        // （优惠额本身先被封顶到行金额，所以这里不会出现负数，只会出现 0）
        var activity = Activity(1, ActivityTypes.FullReduction, threshold: 0m, amount: 100m);
        var result = PromotionCalculator.Calculate(
            new[] { Line(100, 1001, 100m) }, new[] { activity },
            Array.Empty<UserCoupon>(), MarketingPriorities.CouponFirst, Now);

        Assert.Equal(0.01m, result.FinalPrice);
        Assert.True(result.Lines[0].IsFloored);
    }

    [Fact]
    public void 没有活动也没有券时按原价()
    {
        var result = PromotionCalculator.Calculate(
            new[] { Line(100, 1001, 30m) },
            Array.Empty<PromotionActivity>(), Array.Empty<UserCoupon>(),
            MarketingPriorities.CouponFirst, Now);

        Assert.Equal(30m, result.OriginalTotal);
        Assert.Equal(30m, result.FinalPrice);
        Assert.Equal(DiscountSources.None, result.Lines[0].Source);
    }

    [Fact]
    public void 满赠命中时角标显示赠但金额不变()
    {
        var gift = Activity(1, ActivityTypes.Gift, threshold: 50m);
        var result = PromotionCalculator.Calculate(
            new[] { Line(100, 1001, 100m) }, new[] { gift },
            Array.Empty<UserCoupon>(), MarketingPriorities.CouponFirst, Now);

        Assert.Equal(100m, result.FinalPrice);
        Assert.Equal(0m, result.ActivityDiscountTotal);
        Assert.Equal(DiscountSources.Gift, result.Lines[0].Source);
        Assert.Equal("活动1", result.Lines[0].SourceName);
    }

    [Fact]
    public void 解析坏的适用范围JSON时活动对谁都不生效而不是抛异常()
    {
        // 写坏 JSON 的后果应该是「这个活动对谁都无效」，而不是整个下单页 500
        var broken = Activity(1, ActivityTypes.FullReduction, threshold: 0m, amount: 20m,
            targetType: TargetTypes.BySku, targets: "{这不是JSON");

        var quote = PromotionCalculator.Quote(broken, new[] { Line(100, 1001, 100m) }, Now);

        Assert.False(quote.Applicable);
    }
}