using MarketingService.Domain.Entities;
using MarketingService.Domain.Services;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>券计算的单元测试（纯函数，不需要数据库）。</summary>
/// <remarks>
/// 券算错钱是资损级事故，所以计算规则必须有单元测试兜着，
/// 不能只靠 e2e 点点界面发现。
/// </remarks>
public class CouponCalculatorTests
{
    private static UserCoupon MakeCoupon(
        int type = CouponTypes.FullReduction,
        decimal threshold = 0m,
        decimal discountAmount = 0m,
        decimal discountRate = 0m,
        int targetType = TargetTypes.All,
        string targets = "[]",
        DateTime? expireAt = null,
        long id = 1,
        string code = "C1")
        => new()
        {
            Id = id,
            CouponCode = code,
            CouponType = type,
            ThresholdAmount = threshold,
            DiscountAmount = discountAmount,
            DiscountRate = discountRate,
            ValidDays = 30,
            TargetType = targetType,
            Targets = targets,
            ExpireAt = expireAt ?? DateTime.UtcNow.AddDays(10)
        };

    private static readonly CouponOrderLine[] TwoLines =
    [
        new CouponOrderLine(100L, 1001L, 200m),   // SPU 100 / SKU 1001 / 200
        new CouponOrderLine(200L, 2001L, 300m)    // SPU 200 / SKU 2001 / 300
    ];

    [Fact]
    public void 满减券_无门槛_直接减面额()
    {
        var quote = CouponCalculator.Quote(MakeCoupon(discountAmount: 50m), TwoLines);
        Assert.Equal(50m, quote.DiscountAmount);
        Assert.True(quote.ReachedThreshold);
    }

    [Fact]
    public void 满减券_门槛按适用行金额合计判定_不是订单总额()
    {
        // 全场门槛 400 → 合计 500 达到
        var hit = CouponCalculator.Quote(MakeCoupon(threshold: 400m, discountAmount: 50m), TwoLines);
        Assert.True(hit.ReachedThreshold);
        Assert.Equal(500m, hit.ThresholdBase);

        // 门槛 600 → 不够
        var miss = CouponCalculator.Quote(MakeCoupon(threshold: 600m, discountAmount: 50m), TwoLines);
        Assert.False(miss.ReachedThreshold);
        Assert.Equal(0m, miss.DiscountAmount);
    }

    [Fact]
    public void 门槛基数只算券作用域内的行()
    {
        // 只对 SPU 100 生效的券：适用行只有 200 那行，门槛不能用全单 500 去判
        var coupon = MakeCoupon(
            threshold: 400m, discountAmount: 30m,
            targetType: TargetTypes.BySpu, targets: "[100]");

        var quote = CouponCalculator.Quote(coupon, TwoLines);

        Assert.Equal(200m, quote.ThresholdBase);   // 只算 SPU 100 的那一行
        Assert.False(quote.ReachedThreshold);      // 200 < 400，够不着
    }

    [Fact]
    public void 折扣券_折扣率是数值_85折即减15()
    {
        // 8.5 表示 85 折 → 减 15%
        var quote = CouponCalculator.Quote(MakeCoupon(type: CouponTypes.Discount, discountRate: 8.5m), TwoLines);
        Assert.Equal(75m, quote.DiscountAmount);   // 500 × 0.15
    }

    [Fact]
    public void 折扣券_一折即减九成()
    {
        var quote = CouponCalculator.Quote(MakeCoupon(type: CouponTypes.Discount, discountRate: 1.0m), TwoLines);
        Assert.Equal(450m, quote.DiscountAmount);  // 500 × 0.9
    }

    [Fact]
    public void 优惠额超过适用行金额时封顶_不产生负数()
    {
        // 面额 999，适用行合计只有 500
        var quote = CouponCalculator.Quote(MakeCoupon(discountAmount: 999m), TwoLines);
        Assert.Equal(500m, quote.DiscountAmount);
    }

    [Fact]
    public void 满赠券折扣额记0()
    {
        var quote = CouponCalculator.Quote(MakeCoupon(type: CouponTypes.Gift, threshold: 100m), TwoLines);
        Assert.Equal(0m, quote.DiscountAmount);
    }

    [Fact]
    public void 代金券可无门槛()
    {
        var quote = CouponCalculator.Quote(MakeCoupon(type: CouponTypes.Cash, discountAmount: 30m), TwoLines);
        Assert.Equal(30m, quote.DiscountAmount);
        Assert.True(quote.ReachedThreshold);
    }

    [Fact]
    public void 指定SKU的券只对该SKU的行生效()
    {
        var coupon = MakeCoupon(
            discountAmount: 100m,
            targetType: TargetTypes.BySku, targets: "[2001]");

        Assert.Equal(300m, CouponCalculator.ResolveApplicableAmount(coupon, TwoLines));

        var hit = CouponCalculator.Quote(coupon, TwoLines);
        Assert.Equal(100m, hit.DiscountAmount);   // 面额 100，但适用行只有 300，封顶后仍是 100
    }

    [Fact]
    public void 券不覆盖的行不计入门槛()
    {
        var coupon = MakeCoupon(
            threshold: 250m, discountAmount: 10m,
            targetType: TargetTypes.BySku, targets: "[2001]");

        var quote = CouponCalculator.Quote(coupon, TwoLines);
        Assert.Equal(300m, quote.ThresholdBase);
        Assert.True(quote.ReachedThreshold);
    }

    [Fact]
    public void 金额四舍五入到两位而不是银行家舍入()
    {
        // 2.345 用 AwayFromZero 应得 2.35；默认 ToEven 会得 2.34
        Assert.Equal(2.35m, CouponCalculator.Round2(2.345m));
        Assert.Equal(2.34m, CouponCalculator.Round2(2.344m));
    }

    [Fact]
    public void 最优券取优惠额最大的()
    {
        var coupons = new List<UserCoupon>
        {
            MakeCoupon(discountAmount: 30m, id: 1, code: "small"),
            MakeCoupon(discountAmount: 80m, id: 2, code: "big"),
            MakeCoupon(discountAmount: 50m, id: 3, code: "mid")
        };

        var ok = CouponCalculator.TryPickBest(coupons, TwoLines, out var best);

        Assert.True(ok);
        Assert.Equal("big", best!.Value.CouponCode);
    }

    [Fact]
    public void 最优券并列时取最临期的那张()
    {
        // 两张都是减 50，但 A 还有 10 天到期、B 只剩 3 天 → 选 B
        var coupons = new List<UserCoupon>
        {
            MakeCoupon(discountAmount: 50m, expireAt: DateTime.UtcNow.AddDays(10), id: 1, code: "later"),
            MakeCoupon(discountAmount: 50m, expireAt: DateTime.UtcNow.AddDays(3), id: 2, code: "sooner")
        };

        CouponCalculator.TryPickBest(coupons, TwoLines, out var best);

        Assert.Equal("sooner", best!.Value.CouponCode);
    }

    [Fact]
    public void 没达门槛的券不参与最优券评选()
    {
        var coupons = new List<UserCoupon>
        {
            // 面额大但够不着门槛
            MakeCoupon(threshold: 9999m, discountAmount: 500m, id: 1, code: "unreachable"),
            MakeCoupon(discountAmount: 20m, id: 2, code: "usable")
        };

        CouponCalculator.TryPickBest(coupons, TwoLines, out var best);

        Assert.Equal("usable", best!.Value.CouponCode);
    }

    [Fact]
    public void 全部券都不可用时不选券()
    {
        var coupons = new List<UserCoupon>
        {
            MakeCoupon(threshold: 9999m, discountAmount: 500m, id: 1),
            MakeCoupon(type: CouponTypes.Gift, id: 2)   // 满赠折扣额 0，不算优惠
        };

        Assert.False(CouponCalculator.TryPickBest(coupons, TwoLines, out var best));
        Assert.Null(best);
    }

    [Fact]
    public void 满赠券在有折扣券可用时不占用最优位()
    {
        var coupons = new List<UserCoupon>
        {
            MakeCoupon(type: CouponTypes.Gift, id: 1, code: "gift"),
            MakeCoupon(discountAmount: 25m, id: 2, code: "real")
        };

        CouponCalculator.TryPickBest(coupons, TwoLines, out var best);

        Assert.Equal("real", best!.Value.CouponCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("[1,2")]
    [InlineData("{}")]
    public void Targets_解析不出内容时返回空集合而不是抛异常(string json)
    {
        // 运营手填的 JSON 写错了，不能让整个结算页 500
        Assert.Empty(CouponCalculator.ParseTargets(json));
    }

    [Fact]
    public void Targets_正常JSON能解析()
    {
        var set = CouponCalculator.ParseTargets("[100, 200, 300]");
        Assert.Equal(3, set.Count);
        Assert.Contains(100L, set);

        // 重复 Id 会去重：同一个 SPU 不该在作用域里出现两次，否则按行分摊会被算两遍
        Assert.Equal(2, CouponCalculator.ParseTargets("[100, 200, 200]").Count);
    }
}
