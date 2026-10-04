using PaymentService.Domain.Services;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>退款规则的单元测试（依据 BUSINESS.md 10.2）。</summary>
/// <remarks>
/// 这组用例守的是<strong>资损边界</strong>：窗口放太宽会让虚拟商品签收后还能退，
/// 金额放太松会允许累计退款超过实付。两边都直接对应赔钱。
/// </remarks>
public class RefundRulesTests
{
    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    public void 虚拟商品_待发货或待收货_可退(int status)
    {
        // 这两条正是之前被误判为「虚拟商品完全不可退」的状态。
        // 虚拟订单支付后、商户发货前就停在 20 待发货——买家在这段时间里
        // 连「一直没发货」都无法反馈，只能干等
        Assert.True(RefundRules.CanRefund(status, DeliveryTypeNumbers.Virtual));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(50)]
    [InlineData(60)]
    public void 虚拟商品_签收后不可退(int status)
        => Assert.False(RefundRules.CanRefund(status, DeliveryTypeNumbers.Virtual));

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(50)]
    public void 实物快递与自提_各阶段全程可退(int status)
    {
        // 实物有物流链路可查证，签收后仍有「少件 / 破损 / 与描述不符」的争议
        Assert.True(RefundRules.CanRefund(status, DeliveryTypeNumbers.Express));
        Assert.True(RefundRules.CanRefund(status, DeliveryTypeNumbers.SelfPickup));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    public void 未付款或已整单退过_一律不可退(int status)
    {
        // 与配送方式无关：没付钱没得退；退过了不能退第二次
        Assert.False(RefundRules.CanRefund(status, DeliveryTypeNumbers.Virtual));
        Assert.False(RefundRules.CanRefund(status, DeliveryTypeNumbers.Express));
    }

    [Fact]
    public void 部分退款同样受窗口限制_不是只拦整单退()
    {
        // 「签收后不可退款，<b>含部分退款</b>」——规格 10.2 明确写了「含」字。
        // 只在整单退的路径上校验，部分退就绕过去了
        Assert.False(RefundRules.CanRefund(OrderStatusNumbers.Completed, DeliveryTypeNumbers.Virtual));
    }

    [Fact]
    public void 累计退款不超过实付()
    {
        Assert.True(RefundRules.WithinRefundableBalance(100m, 100m, 0m));
        Assert.True(RefundRules.WithinRefundableBalance(60m, 100m, 40m));
        Assert.False(RefundRules.WithinRefundableBalance(101m, 100m, 0m));
        Assert.False(RefundRules.WithinRefundableBalance(60m, 100m, 41m));
    }

    [Fact]
    public void 累计退款容忍一分钱误差_不是放过钱()
    {
        // 两次部分退款各自除不尽时，末次很可能差一分钱。
        // 一分钱相对订单金额可忽略，但「明明能退却提示超额」会造成投诉
        Assert.True(RefundRules.WithinRefundableBalance(33.34m, 100m, 66.67m));
    }

    [Fact]
    public void 逐行退款不超过该行可退余额()
    {
        Assert.True(RefundRules.WithinLineBalance(50m, 60m, 10m));
        Assert.False(RefundRules.WithinLineBalance(51m, 60m, 10m));
    }

    [Fact]
    public void 退款金额四舍五入到两位小数()
    {
        // 不用银行家舍入：ToEven 会把 2.345 舍成 2.34，退款金额上会引发对账差异
        Assert.Equal(2.35m, RefundRules.Round2(2.345m));
        Assert.Equal(2.34m, RefundRules.Round2(2.344m));
    }
}
