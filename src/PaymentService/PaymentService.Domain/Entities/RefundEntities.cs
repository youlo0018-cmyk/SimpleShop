using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace PaymentService.Domain.Entities;

/// <summary>退款单。</summary>
/// <remarks>
/// 退款是<b>先申请、再审批</b>的两段式：申请时不动订单、不回补库存，
/// 只有审批通过才真正生效（规格 10.2）。这样「运营误点申请」不会立刻造成资损。
/// </remarks>
[Table(Name = "refund_order")]
public class RefundOrder : AdminEntityBase
{
    /// <summary>退款单号，业务唯一。</summary>
    [Column(Name = "refund_no", StringLength = 32)]
    public string RefundNo { get; set; } = string.Empty;

    /// <summary>订单 Id。</summary>
    [Column(Name = "order_id")]
    public long OrderId { get; set; }

    /// <summary>订单号。</summary>
    [Column(Name = "order_no", StringLength = 64)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>下单客户 Id。</summary>
    [Column(Name = "customer_id")]
    public long CustomerId { get; set; }

    /// <summary>客户昵称快照。</summary>
    [Column(Name = "customer_name", StringLength = 64)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>申请金额（各行之和），两位小数。</summary>
    [Column(Name = "amount")]
    public decimal Amount { get; set; }

    /// <summary>退款类型，见 <see cref="RefundTypes"/>。</summary>
    [Column(Name = "refund_type")]
    public int RefundType { get; set; } = RefundTypes.Whole;

    /// <summary>退款单状态，见 <see cref="RefundStatuses"/>。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = RefundStatuses.PendingApproval;

    /// <summary>申请原因，2~200 字符。</summary>
    [Column(Name = "reason", StringLength = 500)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>拒绝原因，2~200 字符。通过时为空。</summary>
    [Column(Name = "reject_reason", StringLength = 500)]
    public string RejectReason { get; set; } = string.Empty;

    /// <summary>审批人 Id。</summary>
    [Column(Name = "approver_id")]
    public long ApproverId { get; set; }

    /// <summary>审批人姓名快照。</summary>
    [Column(Name = "approver_name", StringLength = 64)]
    public string ApproverName { get; set; } = string.Empty;

    /// <summary>审批时间 UTC。未审批为 null。</summary>
    [Column(Name = "approved_at")]
    public DateTime? ApprovedAt { get; set; }
}

/// <summary>退款单明细（按订单行退）。</summary>
[Table(Name = "refund_order_item")]
public class RefundOrderItem : AdminEntityBase
{
    /// <summary>退款单 Id。</summary>
    [Column(Name = "refund_id")]
    public long RefundId { get; set; }

    /// <summary>订单行 Id。</summary>
    [Column(Name = "order_item_id")]
    public long OrderItemId { get; set; }

    /// <summary>SKU Id。</summary>
    [Column(Name = "sku_id")]
    public long SkuId { get; set; }

    /// <summary>商品名<b>快照</b>：商品改名后历史退款单要显示当时的名字。</summary>
    [Column(Name = "product_name", StringLength = 128)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格快照。</summary>
    [Column(Name = "sku_spec_text", StringLength = 256)]
    public string SkuSpecText { get; set; } = string.Empty;

    /// <summary>退款数量。</summary>
    [Column(Name = "quantity")]
    public int Quantity { get; set; }

    /// <summary>该行退款金额，两位小数。</summary>
    [Column(Name = "amount")]
    public decimal Amount { get; set; }
}

/// <summary>退款类型。</summary>
public static class RefundTypes
{
    /// <summary>整单退。<b>含运费</b>。</summary>
    public const int Whole = 1;

    /// <summary>部分退。<b>不退运费</b>（规格 10.2）。</summary>
    public const int Partial = 2;

    /// <summary>取中文名。</summary>
    /// <param name="refundType">退款类型。</param>
    /// <returns>中文名，未知值返回「未知」。</returns>
    public static string NameOf(int refundType) => refundType switch
    {
        Whole => "整单退款",
        Partial => "部分退款",
        _ => "未知"
    };
}

/// <summary>退款单状态。</summary>
public static class RefundStatuses
{
    /// <summary>待审批。</summary>
    public const int PendingApproval = 10;

    /// <summary>已退款（审批通过并已生效）。</summary>
    public const int Refunded = 20;

    /// <summary>已拒绝。<b>无任何副作用</b>。</summary>
    public const int Rejected = 90;

    /// <summary>取中文名。</summary>
    /// <param name="status">状态值。</param>
    /// <returns>中文名，未知值返回「未知」。</returns>
    public static string NameOf(int status) => status switch
    {
        PendingApproval => "待审批",
        Refunded => "已退款",
        Rejected => "已拒绝",
        _ => "未知"
    };
}
