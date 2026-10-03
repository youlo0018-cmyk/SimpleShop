using OrderService.Domain.Services;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>下单金额计算的单元测试（纯函数，不需要数据库）。</summary>
/// <remarks>
/// 金额算错是直接的资损，而且现场往往查不出来——用户看到「差一分钱」，
/// 订单却完全正常。所以这套口径必须有单元测试兜着，不能靠 e2e 点页面发现。
/// </remarks>
public class OrderAmountCalculatorTests
{
    private static readonly OrderLineInput[] OneLine = [new(1001L, 2, 25.50m)];   // 原行 51.00
    private static readonly OrderLineInput[] TwoLines =
    [
        new(1001L, 2, 25.50m),   // 51.00
        new(2001L, 1, 49.00m)    // 49.00
    ];                                                              // 合计 100.00

    private static OrderAmount Calc(
        IReadOnlyList<OrderLineInput>? lines = null,
        decimal couponTotal = 0m,
        decimal activityTotal = 0m,
        FreightRule freight = default,
        long points = 0)
    {
        var l = lines ?? OneLine;
        return OrderAmountCalculator.Calculate(
            l,
            OrderAmountCalculator.AllocateCouponDiscount(l, couponTotal),
            AllocateEvenly(activityTotal, l.Count),
            freight, points);
    }

    private static decimal[] AllocateEvenly(decimal total, int count)
    {
        var r = new decimal[count];
        if (count == 0) return r;
        r[0] = total;
        return r;
    }

    [Fact]
    public void 原行金额等于售价乘数量()
    {
        var r = Calc();
        Assert.Equal(51.00m, r.Lines[0].OriginalAmount);
        Assert.Equal(51.00m, r.GoodsTotal);
    }

    [Fact]
    public void 无优惠无运费时实付等于商品总额()
    {
        var r = Calc();
        Assert.Equal(51.00m, r.PayableAmount);
    }

    [Fact]
    public void 商品总额由各行累加而非总减总()
    {
        // 两行 51.00 + 49.00 = 100.00
        var r = Calc(TwoLines);
        Assert.Equal(100.00m, r.GoodsTotal);
    }

    [Fact]
    public void 券优惠按比例分摊到行且总和等于整单优惠()
    {
        var alloc = OrderAmountCalculator.AllocateCouponDiscount(TwoLines, 33.33m);
        Assert.Equal(33.33m, alloc.Sum());
    }

    [Fact]
    public void 分摊余数不会丢失()
    {
        // 三行按比例分摊一个除不尽的数
        var lines = new[] { new OrderLineInput(1, 1, 10.00m), new OrderLineInput(2, 1, 10.00m), new OrderLineInput(3, 1, 10.01m) };
        var alloc = OrderAmountCalculator.AllocateCouponDiscount(lines, 10.00m);
        // 各行之和必须精确等于整单优惠，否则对账会差钱
        Assert.Equal(10.00m, alloc.Sum());
    }

    [Fact]
    public void 券把行压到0时封底到001()
    {
        // 售价 25.50 × 2 = 51.00，券减 51.00 → 0 → 抬到 0.01
        var r = Calc(couponTotal: 51.00m);
        Assert.Equal(0.01m, r.Lines[0].PayableAmount);
        Assert.True(r.Lines[0].IsFloored);
    }

    [Fact]
    public void 只有积分能把订单打到000()
    {
        // 券用满 0.01 后，再用积分抵扣这 0.01
        var r = Calc(couponTotal: 51.00m, points: 1);   // 1 积分 = 0.01 元
        Assert.Equal(0.00m, r.PayableAmount);
    }

    [Fact]
    public void 积分抵扣上限是应付商品金额的100倍不会算成负数()
    {
        // 试图用 9999 积分抵扣 51.00 的单（上限本该在应用层限制，这里兜底不出现负数）
        var r = Calc(points: 9999);
        Assert.Equal(0.00m, r.PayableAmount);   // 下限 0.00
    }

    [Fact]
    public void 积分抵扣等于积分数除以100()
    {
        // 250 积分 = 2.50 元
        var r = Calc(points: 250);
        Assert.Equal(2.50m, r.PointsDeduction);
        Assert.Equal(48.50m, r.PayableAmount);   // 51.00 − 2.50
    }

    [Fact]
    public void 实付等于商品总额加运费减积分抵扣()
    {
        var r = Calc(TwoLines, points: 100, freight: new FreightRule(10m));
        Assert.Equal(100.00m, r.GoodsTotal);
        Assert.Equal(10.00m, r.Freight);
        Assert.Equal(1.00m, r.PointsDeduction);
        Assert.Equal(109.00m, r.PayableAmount);   // 100 + 10 − 1
    }

    [Fact]
    public void 达到包邮门槛则免运费()
    {
        var r = Calc(TwoLines, freight: new FreightRule(10m, 100m));
        Assert.Equal(0m, r.Freight);
        Assert.Equal(100.00m, r.PayableAmount);
    }

    [Fact]
    public void 未达包邮门槛照收运费()
    {
        var r = Calc(TwoLines, freight: new FreightRule(10m, 200m));
        Assert.Equal(10m, r.Freight);
        Assert.Equal(110.00m, r.PayableAmount);
    }

    [Fact]
    public void 活动优惠与券优惠可以叠加()
    {
        var r = Calc(activityTotal: 10m, couponTotal: 11m);
        // 51.00 − 10.00 − 11.00 = 30.00
        Assert.Equal(10.00m, r.Lines[0].ActivityDiscount);
        Assert.Equal(11.00m, r.Lines[0].CouponDiscount);
        Assert.Equal(30.00m, r.PayableAmount);
    }

    [Fact]
    public void 负的优惠额按0处理()
    {
        var r = Calc(activityTotal: -5m);
        Assert.Equal(0m, r.Lines[0].ActivityDiscount);
        Assert.Equal(51.00m, r.PayableAmount);
    }

    [Fact]
    public void 舍入用四舍五入而不是银行家舍入()
    {
        // 0.125 用 ToEven 会得 0.12，四舍五入应得 0.13
        Assert.Equal(0.13m, OrderAmountCalculator.Round2(0.125m));
        Assert.Equal(2.35m, OrderAmountCalculator.Round2(2.345m));
        Assert.Equal(0.12m, OrderAmountCalculator.Round2(0.124m));
    }

    [Fact]
    public void 优惠数组长度不匹配时抛异常而不是静默算错()
    {
        // 长度对不上却继续算，会把别的行的钱减到这一行——宁可炸掉
        Assert.Throws<ArgumentException>(() =>
            OrderAmountCalculator.Calculate(TwoLines, new[] { 1m }, new[] { 0m, 0m }));
    }

    [Fact]
    public void 多行各自被券压到0时各抬到001()
    {
        // 直接给每行一个等于原行金额的券优惠，而不是靠整单分摊——
        // 分摊会把整单优惠全给金额最大的那一行，测不到「每行都封底」这个场景
        var r = OrderAmountCalculator.Calculate(
            TwoLines,
            new[] { 51.00m, 49.00m },
            new[] { 0m, 0m });

        Assert.Equal(0.01m, r.Lines[0].PayableAmount);
        Assert.Equal(0.01m, r.Lines[1].PayableAmount);
        Assert.Equal(0.02m, r.GoodsTotal);
        Assert.Equal(2, r.Lines.Count(a => a.IsFloored));
    }

    [Fact]
    public void 整单优惠按比例分摊时不会把优惠全堆到最大行()
    {
        // 整单 100.00 券优惠分摊到 51.00 / 49.00 两行：
        // 51.00 × 100/100 = 51.00 落到第一行，49.00 落到第二行（余数给最大行）
        var alloc = OrderAmountCalculator.AllocateCouponDiscount(TwoLines, 100.00m);
        Assert.Equal(51.00m, alloc[0]);
        Assert.Equal(49.00m, alloc[1]);
        Assert.Equal(100.00m, alloc.Sum());
    }

    [Fact]
    public void 数量为0的行不参与封底()
    {
        var lines = new[] { new OrderLineInput(1, 0, 25.50m) };
        var r = OrderAmountCalculator.Calculate(lines, new[] { 0m }, new[] { 0m });
        // 原行就是 0，不该被抬成 0.01
        Assert.Equal(0m, r.Lines[0].PayableAmount);
        Assert.False(r.Lines[0].IsFloored);
    }
}
