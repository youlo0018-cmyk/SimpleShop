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
