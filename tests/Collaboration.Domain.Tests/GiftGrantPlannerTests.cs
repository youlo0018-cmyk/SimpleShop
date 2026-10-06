using MarketingService.Domain.Entities;
using MarketingService.Domain.Services;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>满赠发放承诺判定的单元测试。</summary>
/// <remarks>
/// 保护的是「满赠到底送不送、送几张」。判定错了的症状很隐蔽：
/// 少送是用户投诉（下单页写着送券、券包里没有），多送是直接的钱。
/// </remarks>
public class GiftGrantPlannerTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static PromotionActivity Activity(
        long id, int type = ActivityTypes.Gift,
        decimal threshold = 0m, decimal amount = 0m,
        long giftTemplateId = 900, int giftQuantity = 1,
        DateTime? start = null, DateTime? end = null)
        => new()
        {
            Id = id,
            ActivityName = $"活动{id}",
            ActivityType = type,
            ThresholdAmount = threshold,
            DiscountAmount = amount,
            GiftTemplateId = giftTemplateId,
            GiftQuantity = giftQuantity,
            StartTime = start ?? Now.AddDays(-1),
            EndTime = end ?? Now.AddDays(1),
            CreatedAt = Now.AddDays(-2),
            Status = 1
        };

    private static CouponTemplate Template(long id, int totalQuantity, int issuedQuantity)
        => new()
        {
            Id = id,
            TemplateName = $"模板{id}",
            CouponType = CouponTypes.FullReduction,
            ThresholdAmount = 0m,
            DiscountAmount = 10m,
            ValidDays = 30,
            TotalQuantity = totalQuantity,
            IssuedQuantity = issuedQuantity,
            PerUserLimit = 1,
            PerOrderLimit = 1,
            Status = 1
        };

    [Fact]
    public void 满赠命中时能定位到赠送活动与张数()
    {
        var activities = new[] { Activity(7, giftTemplateId: 901, giftQuantity: 3) };
        var lines = new[] { new PromotionLine(100, 1001, 200m) };

        var hit = GiftGrantPlanner.FindHitGiftActivity(activities, lines, Now, giftHit: true);

        Assert.NotNull(hit);
        Assert.Equal(7, hit!.Id);
        Assert.Equal(901, hit.GiftTemplateId);
        Assert.Equal(3, hit.GiftQuantity);
    }

    [Fact]
    public void 优惠引擎没判命中时不承诺()
    {
        // 有满减可用时满赠让位（BUSINESS.md 11.2），此时不能承诺赠品券
        var activities = new[] { Activity(7) };
        var lines = new[] { new PromotionLine(100, 1001, 200m) };

        Assert.Null(GiftGrantPlanner.FindHitGiftActivity(activities, lines, Now, giftHit: false));
    }

    [Fact]
    public void 有正数折扣活动时满赠不中_即使调用方误报命中()
    {
        // 兜底：即使上游把 giftHit 传成 true，这里也会发现真正命中的是满减而不是满赠。
        // 两个判断各写一份的话，就会出现「引擎说命中满减、发券说命中满赠」。
        var activities = new[]
        {
            Activity(7, ActivityTypes.FullReduction, threshold: 100m, amount: 20m),
            Activity(8, ActivityTypes.Gift)
        };
        var lines = new[] { new PromotionLine(100, 1001, 200m) };

        Assert.Null(GiftGrantPlanner.FindHitGiftActivity(activities, lines, Now, giftHit: true));
    }

    [Fact]
    public void 未达门槛的满赠不承诺()
    {
        var activities = new[] { Activity(7, threshold: 500m) };
        var lines = new[] { new PromotionLine(100, 1001, 200m) };

        Assert.Null(GiftGrantPlanner.FindHitGiftActivity(activities, lines, Now, giftHit: true));
    }

    [Fact]
    public void 已过期的满赠不承诺()
    {
        var activities = new[] { Activity(7, end: Now.AddHours(-1)) };
        var lines = new[] { new PromotionLine(100, 1001, 200m) };

        Assert.Null(GiftGrantPlanner.FindHitGiftActivity(activities, lines, Now, giftHit: true));
    }

    [Fact]
    public void 赠送模板不存在时不承诺()
    {
        Assert.False(GiftGrantPlanner.CanPromise(null, 1));
    }

    [Fact]
    public void 赠送张数不为正时不承诺()
    {
        Assert.False(GiftGrantPlanner.CanPromise(Template(900, 100, 0), 0));
    }

    [Fact]
    public void 不限量的模板可以承诺()
    {
        // TotalQuantity = 0 表示不限量（DATA_SPEC 5.12）
        Assert.True(GiftGrantPlanner.CanPromise(Template(900, 0, 9999), 5));
    }

    [Fact]
    public void 池子不够时不承诺()
    {
        var template = Template(900, totalQuantity: 100, issuedQuantity: 98);

        Assert.True(GiftGrantPlanner.CanPromise(template, 2));
        Assert.False(GiftGrantPlanner.CanPromise(template, 3));
    }
}
