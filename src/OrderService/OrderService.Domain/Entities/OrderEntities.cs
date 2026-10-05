using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace OrderService.Domain.Entities;

/// <summary>订单主表。</summary>
/// <remarks>
/// 金额字段全部保留两位小数，<b>入库前已在应用层舍好</b>（BUSINESS.md 8.4）。
/// PostgreSQL 的 numeric→numeric(18,2) 走银行家舍入，依赖列类型会差一分钱。
/// </remarks>
[Table(Name = """order""")]
public class Order : EntityBase
{
    /// <summary>订单号，业务唯一。格式 yyyyMMddHHmmss + 随机段。</summary>
    [Column(Name = "order_no", StringLength = 64)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>客户 Id。</summary>
    [Column(Name = "customer_id")]
    public long CustomerId { get; set; }

    /// <summary>客户唯一编码快照，后台按客户编码检索订单。</summary>
    [Column(Name = "customer_no", StringLength = 64)]
    public string CustomerNo { get; set; } = string.Empty;

    /// <summary>平台 Id。</summary>
    [Column(Name = "platform_id")]
    public long PlatformId { get; set; }

    /// <summary>商户 Id。</summary>
    [Column(Name = "merchant_id")]
    public long MerchantId { get; set; }

    /// <summary>订单状态，见 <see cref="OrderStatuses"/>。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = OrderStatuses.PendingPayment;

    /// <summary>商品总额 = Σ 各行应付（由行累加，不重算）。</summary>
    [Column(Name = "goods_total")]
    public decimal GoodsTotal { get; set; }

    /// <summary>运费。</summary>
    [Column(Name = "freight")]
    public decimal Freight { get; set; }

    /// <summary>积分抵扣金额。</summary>
    [Column(Name = "points_deduction")]
    public decimal PointsDeduction { get; set; }

    /// <summary>实付金额 = 商品总额 + 运费 − 积分抵扣。</summary>
    [Column(Name = "payable_amount")]
    public decimal PayableAmount { get; set; }

    /// <summary>本单实际使用的积分（整数）。</summary>
    [Column(Name = "points_used")]
    public long PointsUsed { get; set; }

    /// <summary>占用的用户券 Id，0 表示没用券。</summary>
    [Column(Name = "coupon_id")]
    public long CouponId { get; set; }

    /// <summary>整单券优惠额。</summary>
    [Column(Name = "coupon_discount")]
    public decimal CouponDiscount { get; set; }

    /// <summary>已退金额合计，两位小数。</summary>
    /// <remarks>
    /// <b>冗余字段，但它必须存在</b>：多次部分退款要求每次申请时都能立刻算出
    /// 「还能退多少」。如果每次都去 order_refund_item 上 SUM，
    /// 两个并发退款请求就会同时读到同一个「已退合计」，
    /// 各自都判断「还有余额」，然后一起把订单退成超额 —— 这是实打实的资损。
    ///
    /// <para>写成订单上的累加列之后，判断与累加可以在**同一条 UPDATE** 里做
    /// （<c>WHERE refunded_amount &lt;= payable_amount - 本次金额</c>），
    /// 数据库层面就把超退挡掉了。</para>
    /// </remarks>
    [Column(Name = "refunded_amount")]
    public decimal RefundedAmount { get; set; }

    /// <summary>收货地址快照。下单那一刻的地址，之后改地址不影响已有订单。</summary>
    [Column(Name = "receiver_name", StringLength = 64)]
    public string ReceiverName { get; set; } = string.Empty;

    /// <summary>收货电话快照。</summary>
    [Column(Name = "receiver_phone", StringLength = 20)]
    public string ReceiverPhone { get; set; } = string.Empty;

    /// <summary>收货地址快照。</summary>
    [Column(Name = "receiver_address", StringLength = 256)]
    public string ReceiverAddress { get; set; } = string.Empty;

    /// <summary>幂等键。<b>同一个客户 + 同一个键只能有一张单</b>，重复请求返回首次的订单。</summary>
    [Column(Name = "idempotency_key", StringLength = 64)]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>备注。</summary>
    [Column(Name = "remark", StringLength = 512)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>支付时间 UTC，未支付为 null。</summary>
    /// <remarks>
    /// **报表不能拿 <c>created_at</c> 代替它**：GMV 要的是「这段时间收了多少钱」，
    /// 按下单时间算会把「昨天下单、今天付款」算进昨天，
    /// 于是昨天的日报里这笔钱根本没收过，与支付流水一“对就对不上”。
    /// </remarks>
    [Column(Name = "paid_at")]
    public DateTime? PaidAt { get; set; }

    /// <summary>完成时间 UTC（签收 / 核销完成），未完成为 null。</summary>
    /// <remarks>「完成订单数」按它统计，而不是按状态等于 50 反推。</remarks>
    [Column(Name = "completed_at")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>物流公司 Id，0 表示未发货或无需物流。</summary>
    /// <remarks>
    /// 只存 Id 是不够的：物流公司是可以被改名 / 删除的，
    /// 半年后回头查这一单时，Id 可能已经指向另一家公司了。
    /// 所以下面还要冗余一份名称快照。
    /// </remarks>
    [Column(Name = "logistics_company_id")]
    public long LogisticsCompanyId { get; set; }

    /// <summary>
    /// 物流公司名称<b>快照</b>。
    /// </summary>
    /// <remarks>
    /// 发货那一刻把公司名写死在订单上。之后在「物流公司」里改名或删除，
    /// 历史订单显示的仍然是当时那家 —— 订单是对账凭据，显示的必须是当时的信息。
    /// </remarks>
    [Column(Name = "logistics_company_name", StringLength = 128)]
    public string LogisticsCompanyName { get; set; } = string.Empty;

    /// <summary>物流单号，未发货为空。</summary>
    [Column(Name = "tracking_no", StringLength = 64)]
    public string TrackingNo { get; set; } = string.Empty;

    /// <summary>发货时间 UTC，未发货为 null。</summary>
    /// <remarks>
    /// 单独存一个发货时间而不是用 <c>created_at</c> 推算：
    /// 订单可能当天买、次天才发，用下单时间算出来的时效会差一天。
    /// </remarks>
    [Column(Name = "shipped_at")]
    public DateTime? ShippedAt { get; set; }
}

/// <summary>订单行。</summary>
/// <remarks>
/// 商品名 / 规格 / 单价都是<b>快照</b>：下单之后商品改名或改价，不影响这张订单。
/// 订单是对账凭据，显示的必须是当时买的是什么、多少钱。
/// </remarks>
[Table(Name = "order_item")]
public class OrderItem : EntityBase
{
    /// <summary>所属订单 Id。</summary>
    [Column(Name = "order_id")]
    public long OrderId { get; set; }

    /// <summary>订单号，冗余一份，列表按订单号查更快也更好排查。</summary>
    [Column(Name = "order_no", StringLength = 64)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>SPU Id。</summary>
    [Column(Name = "spu_id")]
    public long SpuId { get; set; }

    /// <summary>SKU Id。</summary>
    [Column(Name = "sku_id")]
    public long SkuId { get; set; }

    /// <summary>商品名快照。</summary>
    [Column(Name = "product_name", StringLength = 128)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格文本快照，如「红色 / M」。</summary>
    [Column(Name = "sku_spec_text", StringLength = 256)]
    public string SkuSpecText { get; set; } = string.Empty;

    /// <summary>单价快照。</summary>
    [Column(Name = "price")]
    public decimal Price { get; set; }

    /// <summary>数量。</summary>
    [Column(Name = "quantity")]
    public int Quantity { get; set; }

    /// <summary>原行金额 = 售价 × 数量。</summary>
    [Column(Name = "original_amount")]
    public decimal OriginalAmount { get; set; }

    /// <summary>该行分摊到的活动优惠额。</summary>
    [Column(Name = "activity_discount")]
    public decimal ActivityDiscount { get; set; }

    /// <summary>该行分摊到的券优惠额。</summary>
    [Column(Name = "coupon_discount")]
    public decimal CouponDiscount { get; set; }

    /// <summary>行应付，已封底 0.01。</summary>
    [Column(Name = "payable_amount")]
    public decimal PayableAmount { get; set; }

    /// <summary>配送方式，1 实物快递 / 2 虚拟商品 / 3 实物自提。</summary>
    [Column(Name = "delivery_type")]
    public int DeliveryType { get; set; } = 1;

    /// <summary>来源类型，见 <see cref="OrderSourceTypes"/>。</summary>
    /// <remarks>
    /// 秒杀订单必须标出来：它的库存<b>在发布场次时就从常规池划走了</b>，
    /// 下单与支付时都不能再动常规库存。这个标记就是那两处的判断依据——
    /// 没有它，秒杀单会在下单时再锁一次常规库存，直接超卖。
    /// </remarks>
    [Column(Name = "source_type")]
    public int SourceType { get; set; } = OrderSourceTypes.Normal;
}

/// <summary>订单行来源。</summary>
public static class OrderSourceTypes
{
    /// <summary>普通下单。</summary>
    public const int Normal = 1;

    /// <summary>限时抢购。库存已在场次发布时划出，下单 / 支付都不再动常规库存。</summary>
    public const int Seckill = 2;
}

/// <summary>订单状态机（BUSINESS.md 7.1）。</summary>
public static class OrderStatuses
{
    /// <summary>待支付。下单成功的初始状态。</summary>
    public const int PendingPayment = 10;

    /// <summary>待发货。支付成功进入；实付 0 元的单直接到这里。</summary>
    public const int PendingShipment = 20;

    /// <summary>待收货。快递 / 虚拟发货后。</summary>
    public const int PendingReceipt = 30;

    /// <summary>待取货。仅自提，商户点「备货完成」进入。</summary>
    public const int PendingPickup = 40;

    /// <summary>已完成。</summary>
    public const int Completed = 50;

    /// <summary>已退款。</summary>
    public const int Refunded = 60;

    /// <summary>已取消。仅可从 10 进入。</summary>
    public const int Cancelled = 91;
}

/// <summary>订单退款记录（后台代客退款）。</summary>
/// <remarks>
/// <b>一张订单可以对应多条退款记录</b>：多次部分退款是这个表的常态，
/// 不是异常。早期实现是一退就把订单打成 60 已退款，于是第二次退款无处落脚，
/// 「退了一件还想要退另一件」这种最常见的诉求直接做不了。
///
/// <para>本表只记「订单侧发生了什么」：退了哪几行、退了多少、库存是否回补。
/// 资金流水在支付服务，两边以订单号对齐（详见 <see cref="OrderRefundTypes"/>）。</para>
/// </remarks>
[Table(Name = "order_refund")]
public class OrderRefund : EntityBase
{
    /// <summary>退款单号，业务唯一。</summary>
    [Column(Name = "refund_no", StringLength = 32)]
    public string RefundNo { get; set; } = string.Empty;

    /// <summary>订单 Id。</summary>
    [Column(Name = "order_id")]
    public long OrderId { get; set; }

    /// <summary>订单号，冗余一份便于按单号直接排查。</summary>
    [Column(Name = "order_no", StringLength = 64)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>平台 Id。</summary>
    [Column(Name = "platform_id")]
    public long PlatformId { get; set; }

    /// <summary>商户 Id。</summary>
    [Column(Name = "merchant_id")]
    public long MerchantId { get; set; }

    /// <summary>客户 Id。</summary>
    [Column(Name = "customer_id")]
    public long CustomerId { get; set; }

    /// <summary>本次退款金额合计，两位小数。</summary>
    [Column(Name = "amount")]
    public decimal Amount { get; set; }

    /// <summary>退款类型，见 <see cref="OrderRefundTypes"/>。</summary>
    [Column(Name = "refund_type")]
    public int RefundType { get; set; } = OrderRefundTypes.Partial;

    /// <summary>退款后订单是否已整单退完。</summary>
    /// <remarks>
    /// 冗余一个布尔而不是每次去算「已退合计 == 实付」：订单详情页要显示
    /// 「已全额退款 / 还可再退多少」，每次现算就得再聚合一次明细表。
    /// </remarks>
    [Column(Name = "fully_refunded")]
    public bool FullyRefunded { get; set; }

    /// <summary>退款原因，2~512 个字符。</summary>
    [Column(Name = "reason", StringLength = 512)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>操作人 Id（后台账号）。</summary>
    [Column(Name = "operator_id")]
    public long OperatorId { get; set; }

    /// <summary>操作人姓名快照。</summary>
    [Column(Name = "operator_name", StringLength = 64)]
    public string OperatorName { get; set; } = string.Empty;
}

/// <summary>退款单明细（按订单行退）。</summary>
[Table(Name = "order_refund_item")]
public class OrderRefundItem : EntityBase
{
    /// <summary>退款记录 Id。</summary>
    [Column(Name = "refund_id")]
    public long RefundId { get; set; }

    /// <summary>订单 Id，便于按订单直接聚合明细。</summary>
    [Column(Name = "order_id")]
    public long OrderId { get; set; }

    /// <summary>订单行 Id。</summary>
    [Column(Name = "order_item_id")]
    public long OrderItemId { get; set; }

    /// <summary>SKU Id，回补库存用。</summary>
    [Column(Name = "sku_id")]
    public long SkuId { get; set; }

    /// <summary>商品名快照：商品改名后历史退款记录要显示当时的名字。</summary>
    [Column(Name = "product_name", StringLength = 128)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格快照。</summary>
    [Column(Name = "sku_spec_text", StringLength = 256)]
    public string SkuSpecText { get; set; } = string.Empty;

    /// <summary>本次退款数量。</summary>
    [Column(Name = "quantity")]
    public int Quantity { get; set; }

    /// <summary>该行本次退款金额，两位小数。</summary>
    [Column(Name = "amount")]
    public decimal Amount { get; set; }
}

/// <summary>订单退款类型。</summary>
public static class OrderRefundTypes
{
    /// <summary>部分退款。只退选中的行 / 行内的一部分金额。</summary>
    public const int Partial = 1;

    /// <summary>整单退款。退了剩余的全部可退金额，订单转为 60 已退款。</summary>
    public const int Whole = 2;

    /// <summary>取中文名。</summary>
    /// <param name="refundType">退款类型。</param>
    /// <returns>中文名，未知值返回「未知」。</returns>
    public static string NameOf(int refundType) => refundType switch
    {
        Partial => "部分退款",
        Whole => "整单退款",
        _ => "未知"
    };
}
