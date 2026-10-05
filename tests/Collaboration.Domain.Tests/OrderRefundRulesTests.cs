using OrderService.Domain.Services;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>订单多次部分退款规则的单元测试。</summary>
/// <remarks>
/// 这组用例守的是<strong>资损边界</strong>：允许超退一次就要人工追回。
/// 重点覆盖三条：行级余额、订单级余额、以及两者不一致时（运费 / 积分抵扣）怎么摊。
/// </remarks>
public class OrderRefundRulesTests
{
    /// <summary>两件商品，各 60 元，订单实付 120 元（无运费无优惠）。</summary>
    private static readonly RefundableLine[] TwoLines =
    [
        new RefundableLine(11, 2, 0, 60m, 0m),
        new RefundableLine(12, 1, 0, 60m, 0m),
    ];

    [Fact]
    public void 整单退_未退过时_退光全部行并标记全额退完()
    {
        var result = OrderRefundRules.Resolve(TwoLines, 120m, 0m, null);

        Assert.True(result.IsValid);
        Assert.Equal(120m, result.Total);
        Assert.Equal(0m, result.RemainingAfter);
        Assert.True(result.FullyRefunded);
        // 行金额之和已经等于订单实付，不该再多出一条运费行
        Assert.Equal(2, result.Lines.Count);
    }

    [Fact]
    public void 整单退_行金额小于订单实付_差额补成运费行()
    {
        // 运费 10 元：订单实付 130，但行金额只有 120。
        // 不补这条运费行的话，订单永远差 10 元退不干净
        var result = OrderRefundRules.Resolve(TwoLines, 130m, 0m, null);

        Assert.True(result.IsValid);
        Assert.Equal(130m, result.Total);
        Assert.True(result.FullyRefunded);

        var freight = result.Lines.Single(a => a.OrderItemId == 0);
        Assert.Equal(10m, freight.Amount);
    }

    [Fact]
    public void 整单退_已部分退过时_只退剩余余额()
    {
        // 第一笔已退 60，订单实付 130 → 还剩 70
        var lines = new[]
        {
            new RefundableLine(11, 2, 1, 60m, 60m),
            new RefundableLine(12, 1, 0, 60m, 0m),
        };
        var result = OrderRefundRules.Resolve(lines, 130m, 60m, null);

        Assert.True(result.IsValid);
        Assert.Equal(70m, result.Total);
        Assert.True(result.FullyRefunded);
        // 第一行钱已退完，不该再出现在本次明细里（否则会被重复回补库存）
        Assert.DoesNotContain(result.Lines, a => a.OrderItemId == 11);
    }

    [Fact]
    public void 部分退_只退指定行_订单不变成全额退完()
    {
        var result = OrderRefundRules.Resolve(
            TwoLines, 120m, 0m, [new RefundLineRequest(11, 1, 60m)]);

        Assert.True(result.IsValid);
        Assert.Equal(60m, result.Total);
        Assert.Equal(60m, result.RemainingAfter);
        // 还剩 60 没退，订单必须继续可退
        Assert.False(result.FullyRefunded);
        var line = Assert.Single(result.Lines);
        Assert.Equal(11, line.OrderItemId);
        Assert.Equal(1, line.Quantity);
    }

    [Fact]
    public void 多次部分退_退完剩余余额后才标记全额退完()
    {
        // 第一次：退第一行 60
        var first = OrderRefundRules.Resolve(
            TwoLines, 120m, 0m, [new RefundLineRequest(11, 1, 60m)]);
        Assert.True(first.IsValid);
        Assert.False(first.FullyRefunded);

        // 第二次：按「已退 60」重新算余额，退第二行 60 → 刚好退完
        var remaining = new[]
        {
            new RefundableLine(11, 2, 1, 60m, 60m),
            new RefundableLine(12, 1, 0, 60m, 0m),
        };
        var second = OrderRefundRules.Resolve(
            remaining, 120m, first.Total, [new RefundLineRequest(12, 1, 60m)]);

        Assert.True(second.IsValid);
        Assert.Equal(60m, second.Total);
        Assert.Equal(0m, second.RemainingAfter);
        Assert.True(second.FullyRefunded);
    }

    [Fact]
    public void 部分退_金额超过该行可退余额_拒绝()
    {
        var result = OrderRefundRules.Resolve(
            TwoLines, 120m, 0m, [new RefundLineRequest(11, 1, 61m)]);

        Assert.False(result.IsValid);
        Assert.Contains("60.00", result.Error);
    }

    [Fact]
    public void 部分退_各行都合法但合计超订单余额_拒绝()
    {
        // 每行 60 都合规，但订单实付只有 100：合计 120 就超了。
        // 总额这一关只能在汇总之后判，只判行级会漏
        var result = OrderRefundRules.Resolve(
            TwoLines, 100m, 0m,
            [new RefundLineRequest(11, 1, 60m), new RefundLineRequest(12, 1, 60m)]);

        Assert.False(result.IsValid);
        Assert.Contains("超过订单剩余可退", result.Error);
    }

    [Fact]
    public void 部分退_数量超过该行剩余件数_拒绝()
    {
        // 第一行共 2 件，已退 1 件 → 只能再退 1 件
        var lines = new[] { new RefundableLine(11, 2, 1, 60m, 60m) };
        var result = OrderRefundRules.Resolve(lines, 70m, 60m, [new RefundLineRequest(11, 2, 30m)]);

        Assert.False(result.IsValid);
        Assert.Contains("最多还能退 1 件", result.Error);
    }

    [Fact]
    public void 部分退_金额为零_拒绝()
    {
        var result = OrderRefundRules.Resolve(
            TwoLines, 120m, 0m, [new RefundLineRequest(11, 1, 0m)]);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void 订单已全额退过_任何退款都被拒绝()
    {
        Assert.False(OrderRefundRules.Resolve(TwoLines, 120m, 120m, null).IsValid);
        Assert.False(OrderRefundRules.Resolve(TwoLines, 120m, 120m,
            [new RefundLineRequest(11, 1, 10m)]).IsValid);
    }

    [Fact]
    public void 行金额之和大于订单实付_按数据异常拒绝而不是照退()
    {
        // 只在积分抵扣被分摊错时才可能出现。照退就等于退了超过订单实付的钱
        var result = OrderRefundRules.Resolve(TwoLines, 100m, 0m, null);

        Assert.False(result.IsValid);
        Assert.Contains("数据异常", result.Error);
    }

    [Fact]
    public void 余额判定允许一分钱的舍入误差()
    {
        // 两次部分退款各自除不尽时，末次很可能差一分钱，
        // 一分钱的误差不该把正当的退款挡在门外
        var lines = new[]
        {
            new RefundableLine(11, 1, 0, 0.335m, 0.335m),
            new RefundableLine(12, 1, 0, 0.335m, 0m),
        };
        var result = OrderRefundRules.Resolve(lines, 0.67m, 0.34m, [new RefundLineRequest(12, 1, 0.34m)]);

        Assert.True(result.IsValid);
        Assert.True(result.FullyRefunded);
    }

    [Fact]
    public void 未指定行的空列表等同于整单退()
    {
        var result = OrderRefundRules.Resolve(TwoLines, 120m, 0m, []);

        Assert.True(result.IsValid);
        Assert.Equal(120m, result.Total);
        Assert.True(result.FullyRefunded);
    }

    [Fact]
    public void 全退已退过的行_数量与余额都不会算成负数()
    {
        var lines = new[] { new RefundableLine(11, 2, 2, 60m, 60m) };
        // 订单实付 70：行已退完，只剩运费 10
        var result = OrderRefundRules.Resolve(lines, 70m, 60m, null);

        // 行已退完，但订单还有运费 10 没退 → 只应该补那条运费行
        Assert.True(result.IsValid);
        var line = Assert.Single(result.Lines);
        Assert.Equal(0, line.OrderItemId);
        Assert.Equal(10m, line.Amount);
    }
}
