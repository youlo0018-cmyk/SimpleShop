using OrderService.Domain.Entities;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>订单状态机迁移表的单元测试。</summary>
/// <remarks>
/// 状态机是订单模块最容易出「越权改状态」的地方：一旦某条非法迁移被放行，
/// 就会出现「已取消的订单被发货」「已完成的订单又被退款」这类直接造成资损的状态。
/// 迁移表是纯函数，全部枚举出来逐条断言，成本极低但能把这张表钉死。
/// </remarks>
public class OrderStatusMachineTests
{
    /// <summary>全部合法迁移。改动状态机时这个清单要一起改，改漏了就是漏了一条授权路径。</summary>
    public static TheoryData<int, int> AllowedTransitions() => new()
    {
        { OrderStatuses.PendingPayment, OrderStatuses.PendingShipment },
        { OrderStatuses.PendingPayment, OrderStatuses.Cancelled },
        { OrderStatuses.PendingShipment, OrderStatuses.PendingReceipt },
        { OrderStatuses.PendingShipment, OrderStatuses.PendingPickup },
        { OrderStatuses.PendingReceipt, OrderStatuses.Completed },
        { OrderStatuses.PendingPickup, OrderStatuses.Completed },
        { OrderStatuses.PendingPayment, OrderStatuses.Refunded },
        { OrderStatuses.PendingShipment, OrderStatuses.Refunded },
        { OrderStatuses.PendingReceipt, OrderStatuses.Refunded },
        { OrderStatuses.PendingPickup, OrderStatuses.Refunded }
    };

    /// <summary>全部状态的两两组合（含非法）都跑一遍。</summary>
    public static TheoryData<int, int> AllPairs()
    {
        var data = new TheoryData<int, int>();
        var all = new[]
        {
            OrderStatuses.PendingPayment, OrderStatuses.PendingShipment, OrderStatuses.PendingReceipt,
            OrderStatuses.PendingPickup, OrderStatuses.Completed, OrderStatuses.Refunded,
            OrderStatuses.Cancelled, 999
        };

        foreach (var from in all)
        foreach (var to in all)
        {
            data.Add(from, to);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void 只有白名单里的迁移被放行(int from, int to)
    {
        var allowed = AllowedTransitions().Select(a => ((int)a[0], (int)a[1])).ToHashSet();
        var expected = allowed.Contains((from, to));

        Assert.Equal(expected, OrderStatusMachine.CanTransit(from, to));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(60)]
    [InlineData(91)]
    public void 终态谁都不许再改(int status)
    {
        Assert.True(OrderStatusMachine.IsTerminal(status));

        // 终态不能迁到任何一个已知状态——已完成的订单被退款是最典型的资损
        foreach (var to in new[]
                 {
                     OrderStatuses.PendingPayment, OrderStatuses.PendingShipment, OrderStatuses.PendingReceipt,
                     OrderStatuses.PendingPickup, OrderStatuses.Completed, OrderStatuses.Refunded,
                     OrderStatuses.Cancelled
                 })
        {
            Assert.False(OrderStatusMachine.CanTransit(status, to));
        }
    }

    [Fact]
    public void 只有待支付能取消()
    {
        Assert.True(OrderStatusMachine.CanCancel(OrderStatuses.PendingPayment));
        Assert.False(OrderStatusMachine.CanCancel(OrderStatuses.PendingShipment));
        Assert.False(OrderStatusMachine.CanCancel(OrderStatuses.PendingReceipt));
        Assert.False(OrderStatusMachine.CanCancel(OrderStatuses.PendingPickup));
    }

    [Fact]
    public void 确认收货只认待收货而不是待取货()
    {
        // 待取货也能迁到已完成，但那是商户核销取货码走的路。
        // 客户页面若也冒出「确认收货」按钮，自提订单就能被客户自己点完成。
        Assert.True(OrderStatusMachine.CanConfirmReceipt(OrderStatuses.PendingReceipt));
        Assert.False(OrderStatusMachine.CanConfirmReceipt(OrderStatuses.PendingPickup));
        Assert.False(OrderStatusMachine.CanConfirmReceipt(OrderStatuses.PendingShipment));
    }

    [Fact]
    public void 实物订单确认收货后仍可退_虚拟订单签收后不可退()
    {
        // BUSINESS.md 10.2：实物快递 / 自提 **{20,30,40,50} 全程可退**；
        // 虚拟商品仅 {20,30}，签收（50）后不可退（用户澄清过 D4 只针对虚拟订单）。
        //
        // 这里原来是反过来的：实物 50 判成不可退、虚拟 20 也判成不可退。
        // 支付服务的 RefundRules 按规格实现，订单服务这条路径却停在旧规则上 ——
        // 同一个业务规则两条路径算出不同答案，而两条路径的单元测试各自「证明」自己是对的。
        Assert.True(OrderStatusMachine.CanRefund(
            OrderStatuses.Completed, new[] { DeliveryTypes.Express }));
        Assert.True(OrderStatusMachine.CanRefund(
            OrderStatuses.Completed, new[] { DeliveryTypes.SelfPickup }));
        Assert.False(OrderStatusMachine.CanRefund(
            OrderStatuses.Completed, new[] { DeliveryTypes.Virtual }));
    }

    [Fact]
    public void 虚拟商品待发货与待收货可退_其余状态不可退()
    {
        // 虚拟订单支付后、商户发货前停在 20 待发货；那段时间买家连「一直没发货」
        // 都无法反馈，只能干等。把窗口卡死等于把正常退款需求逼成投诉。
        Assert.True(OrderStatusMachine.CanRefund(
            OrderStatuses.PendingShipment, new[] { DeliveryTypes.Virtual }));
        Assert.True(OrderStatusMachine.CanRefund(
            OrderStatuses.PendingReceipt, new[] { DeliveryTypes.Virtual }));

        Assert.True(OrderStatusMachine.CanRefund(
            OrderStatuses.PendingShipment, new[] { DeliveryTypes.Express }));

        // 混了虚拟与实物就按虚拟窗口：部分退会让积分 / 库存 / 券的分摊账对不上
        Assert.True(OrderStatusMachine.CanRefund(
            OrderStatuses.PendingShipment, new[] { DeliveryTypes.Express, DeliveryTypes.Virtual }));
        Assert.False(OrderStatusMachine.CanRefund(
            OrderStatuses.Completed, new[] { DeliveryTypes.Express, DeliveryTypes.Virtual }));
    }

    [Fact]
    public void 未付款_已退款_已取消一律不可退()
    {
        foreach (var status in new[]
                 { OrderStatuses.PendingPayment, OrderStatuses.Refunded, OrderStatuses.Cancelled })
        {
            Assert.False(OrderStatusMachine.CanRefund(status, new[] { DeliveryTypes.Express }));
            Assert.False(OrderStatusMachine.CanRefund(status, new[] { DeliveryTypes.Virtual }));
        }
    }

    [Theory]
    [InlineData(10, "待支付")]
    [InlineData(20, "待发货")]
    [InlineData(30, "待收货")]
    [InlineData(40, "待取货")]
    [InlineData(50, "已完成")]
    [InlineData(60, "已退款")]
    [InlineData(91, "已取消")]
    [InlineData(999, "未知状态")]
    public void 状态中文名都有定义(int status, string expected)
        => Assert.Equal(expected, OrderStatusMachine.NameOf(status));

    [Fact]
    public void 纯虚拟订单能被识别()
    {
        Assert.True(OrderStatusMachine.IsVirtualOnly(new[] { DeliveryTypes.Virtual, DeliveryTypes.Virtual }));
        Assert.False(OrderStatusMachine.IsVirtualOnly(new[] { DeliveryTypes.Virtual, DeliveryTypes.Express }));
        Assert.False(OrderStatusMachine.IsVirtualOnly(Array.Empty<int>()));
    }
}
