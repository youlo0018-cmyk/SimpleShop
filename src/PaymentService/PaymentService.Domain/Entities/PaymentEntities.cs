using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace PaymentService.Domain.Entities;

/// <summary>支付单。</summary>
/// <remarks>
/// 继承 <see cref="AdminEntityBase"/>：支付单既是 C 端发起也是后台操作，
/// 但它始终归属于某个订单（进而归属于平台 / 商户），按租户隔离是对的。
/// </remarks>
[Table(Name = "payment_order")]
public class PaymentOrder : AdminEntityBase
{
    /// <summary>支付单号，业务唯一。</summary>
    [Column(Name = "payment_no", StringLength = 32)]
    public string PaymentNo { get; set; } = string.Empty;

    /// <summary>订单 Id。</summary>
    [Column(Name = "order_id")]
    public long OrderId { get; set; }

    /// <summary>订单号。</summary>
    [Column(Name = "order_no", StringLength = 64)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>
    /// 应付金额，<b>服务端反查订单实付</b>（规格 10.1），不接受客户端传入。
    /// </summary>
    /// <remarks>
    /// 存的是下单那一刻的实付<b>快照</b>：订单后来改价、退款重算都不会影响这张已生成的支付单，
    /// 也不会出现「支付时按新价、订单按旧价」的对不上账。
    /// </remarks>
    [Column(Name = "amount")]
    public decimal Amount { get; set; }

    /// <summary>支付单状态，见 <see cref="PaymentStatuses"/>。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = PaymentStatuses.Pending;

    /// <summary>支付渠道，见 <see cref="PaymentChannels"/>。本项目只有模拟支付一个通道。</summary>
    [Column(Name = "channel")]
    public int Channel { get; set; } = PaymentChannels.Simulate;

    /// <summary>支付成功时间 UTC。未支付为 null。</summary>
    [Column(Name = "paid_at")]
    public DateTime? PaidAt { get; set; }

    /// <summary>关闭时间 UTC（支付超时）。</summary>
    [Column(Name = "closed_at")]
    public DateTime? ClosedAt { get; set; }

    /// <summary>失败原因，成功时为空。</summary>
    [Column(Name = "fail_reason", StringLength = 200)]
    public string FailReason { get; set; } = string.Empty;

    /// <summary>备注。</summary>
    [Column(Name = "remark", StringLength = 512)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>支付单状态。</summary>
public static class PaymentStatuses
{
    /// <summary>待支付。</summary>
    public const int Pending = 1;

    /// <summary>已支付。</summary>
    public const int Paid = 20;

    /// <summary>已关闭（支付超时 30 分钟）。</summary>
    public const int Closed = 30;

    /// <summary>取中文名。</summary>
    /// <param name="status">状态值。</param>
    /// <returns>中文名，未知值返回「未知」。</returns>
    public static string NameOf(int status) => status switch
    {
        Pending => "待支付",
        Paid => "已支付",
        Closed => "已关闭",
        _ => "未知"
    };
}

/// <summary>支付渠道。</summary>
public static class PaymentChannels
{
    /// <summary>模拟支付。本项目唯一通道（规格 10.1「单一通道」）。</summary>
    public const int Simulate = 1;

    /// <summary>取中文名。</summary>
    /// <param name="channel">渠道值。</param>
    /// <returns>中文名，未知值返回「未知」。</returns>
    public static string NameOf(int channel) => channel switch
    {
        Simulate => "模拟支付",
        _ => "未知"
    };
}
