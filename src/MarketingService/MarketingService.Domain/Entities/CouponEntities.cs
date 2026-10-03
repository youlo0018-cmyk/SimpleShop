using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace MarketingService.Domain.Entities;

/// <summary>券模板。改模板**不影响已发出的券**。</summary>
[Table(Name = "coupon_template")]
public class CouponTemplate : AdminEntityBase
{
    /// <summary>模板名，2-128 字符。</summary>
    [Column(Name = "template_name", StringLength = 128)]
    public string TemplateName { get; set; } = string.Empty;

    /// <summary>券类型，见 <see cref="CouponTypes"/>。</summary>
    [Column(Name = "coupon_type")]
    public int CouponType { get; set; }

    /// <summary>门槛金额。0 表示无门槛。判定基数是<b>适用行金额合计</b>，不是订单总额。</summary>
    [Column(Name = "threshold_amount")]
    public decimal ThresholdAmount { get; set; }

    /// <summary>优惠金额（满减 / 代金）。</summary>
    [Column(Name = "discount_amount")]
    public decimal DiscountAmount { get; set; }

    /// <summary>折扣率数值，0.01 ~ 10。8.5 表示 85 折。</summary>
    [Column(Name = "discount_rate")]
    public decimal DiscountRate { get; set; }

    /// <summary>满赠时赠送的券模板 Id。</summary>
    [Column(Name = "gift_template_id")]
    public long GiftTemplateId { get; set; }

    /// <summary>领取后 N 天有效（1~3650）。</summary>
    [Column(Name = "valid_days")]
    public int ValidDays { get; set; } = 30;

    /// <summary>总发行池子。0 表示不限量。</summary>
    [Column(Name = "total_quantity")]
    public int TotalQuantity { get; set; }

    /// <summary>累计已发放数。</summary>
    [Column(Name = "issued_quantity")]
    public int IssuedQuantity { get; set; }

    /// <summary>每人限领（1~100）。</summary>
    [Column(Name = "per_user_limit")]
    public int PerUserLimit { get; set; } = 1;

    /// <summary>每单限用。订单级只能一张，默认 1。</summary>
    [Column(Name = "per_order_limit")]
    public int PerOrderLimit { get; set; } = 1;

    /// <summary>排序。</summary>
    [Column(Name = "sort_order")]
    public int SortOrder { get; set; }

    /// <summary>状态。1 启用 / 2 停用。停用后不可领取，已发出的券不受影响。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = 1;
}

/// <summary>券活动（领券中心）。发放量与模板 TotalQuantity 是**两个独立池子**。</summary>
[Table(Name = "coupon_activity")]
public class CouponActivity : AdminEntityBase
{
    /// <summary>券活动名，2-128 字符。</summary>
    [Column(Name = "activity_name", StringLength = 128)]
    public string ActivityName { get; set; } = string.Empty;

    /// <summary>关联的券模板 Id。发放时从这里取一次快照。</summary>
    [Column(Name = "template_id")]
    public long TemplateId { get; set; }

    /// <summary>领取开始时间（UTC）。与券有效期是两个不同概念。</summary>
    [Column(Name = "claim_start_time")]
    public DateTime ClaimStartTime { get; set; }

    /// <summary>领取结束时间（UTC）。</summary>
    [Column(Name = "claim_end_time")]
    public DateTime ClaimEndTime { get; set; }

    /// <summary>本次发放量。</summary>
    [Column(Name = "claim_quantity")]
    public int ClaimQuantity { get; set; } = 1;

    /// <summary>已领取数。</summary>
    [Column(Name = "claimed_quantity")]
    public int ClaimedQuantity { get; set; }

    /// <summary>每人限领。生效值取本值与模板 PerUserLimit 的<b>较小者</b>。</summary>
    [Column(Name = "per_user_limit")]
    public int PerUserLimit { get; set; } = 1;

    /// <summary>适用范围。1 全场 / 2 指定 SPU / 3 指定 SKU。</summary>
    [Column(Name = "target_type")]
    public int TargetType { get; set; } = TargetTypes.All;

    /// <summary>目标列表，JSON Id 数组，最多 200 个。</summary>
    [Column(Name = "targets", StringLength = 2000)]
    public string Targets { get; set; } = "[]";

    /// <summary>排序。</summary>
    [Column(Name = "sort_order")]
    public int SortOrder { get; set; }

    /// <summary>状态。1 启用 / 2 停用。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = 1;
}

/// <summary>用户券（券包）。</summary>
/// <remarks>
/// <b>发放时把模板的券型 / 门槛 / 优惠额 / 折扣率 / 有效天数复制一份快照存到这张券自己的字段上</b>，
/// 之后模板怎么改都不再影响它（DATA_SPEC 5.12）。不这么做的话，
/// 运营改一次模板价格，全站已发出的券全部跟着变价——那是资损级别的事故。
/// </remarks>
[Table(Name = "user_coupon")]
public class UserCoupon : EntityBase
{
    /// <summary>客户 Id。</summary>
    [Column(Name = "customer_id")]
    public long CustomerId { get; set; }

    /// <summary>来源模板 Id。</summary>
    [Column(Name = "template_id")]
    public long TemplateId { get; set; }

    /// <summary>来源券活动 Id，0 表示直接发放。</summary>
    [Column(Name = "activity_id")]
    public long ActivityId { get; set; }

    /// <summary>券码，全局唯一，供客服 / 用户报单使用。</summary>
    [Column(Name = "coupon_code", StringLength = 32)]
    public string CouponCode { get; set; } = string.Empty;

    // ---------- 快照字段：以下 5 个不与模板联动 ----------

    /// <summary>券类型快照。</summary>
    [Column(Name = "coupon_type")]
    public int CouponType { get; set; }

    /// <summary>门槛金额快照。0 表示无门槛。</summary>
    [Column(Name = "threshold_amount")]
    public decimal ThresholdAmount { get; set; }

    /// <summary>优惠金额快照。</summary>
    [Column(Name = "discount_amount")]
    public decimal DiscountAmount { get; set; }

    /// <summary>折扣率快照。</summary>
    [Column(Name = "discount_rate")]
    public decimal DiscountRate { get; set; }

    /// <summary>有效天数快照，从<b>领取时刻</b>起算。</summary>
    [Column(Name = "valid_days")]
    public int ValidDays { get; set; }

    // ---------- 快照结束 ----------

    /// <summary>适用范围。1 全场 / 2 指定 SPU / 3 指定 SKU。</summary>
    [Column(Name = "target_type")]
    public int TargetType { get; set; } = TargetTypes.All;

    /// <summary>目标列表快照，JSON Id 数组。</summary>
    [Column(Name = "targets", StringLength = 2000)]
    public string Targets { get; set; } = "[]";

    /// <summary>状态，见 <see cref="CouponStatuses"/>。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = CouponStatuses.Unused;

    /// <summary>到期时间（UTC），领取时刻 + ValidDays。</summary>
    [Column(Name = "expire_at")]
    public DateTime ExpireAt { get; set; }

    /// <summary>领取时间（UTC）。</summary>
    [Column(Name = "receive_at")]
    public DateTime ReceiveAt { get; set; }

    /// <summary>占用的订单号，未占用时为空。</summary>
    [Column(Name = "order_no", StringLength = 64)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>核销时间（UTC）。</summary>
    [Column(Name = "consume_at")]
    public DateTime? ConsumeAt { get; set; }
}

/// <summary>占券记录。一笔订单最多占一张券（BUSINESS.md 11.3）。</summary>
[Table(Name = "coupon_occupancy")]
public class CouponOccupancy : EntityBase
{
    /// <summary>客户 Id。</summary>
    [Column(Name = "customer_id")]
    public long CustomerId { get; set; }

    /// <summary>订单号。</summary>
    [Column(Name = "order_no", StringLength = 64)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>被占用的用户券 Id。</summary>
    [Column(Name = "coupon_id")]
    public long CouponId { get; set; }

    /// <summary>本单实际优惠了多少（可能小于券面额，受行金额封顶）。</summary>
    [Column(Name = "discount_amount")]
    public decimal DiscountAmount { get; set; }

    /// <summary>状态，见 <see cref="OccupancyStatuses"/>。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = OccupancyStatuses.Occupied;
}

/// <summary>营销配置：平台优惠优先级。每平台一条。</summary>
[Table(Name = "marketing_config")]
public class MarketingConfig : EntityBase
{
    /// <summary>平台 Id。</summary>
    [Column(Name = "platform_id")]
    public long PlatformId { get; set; }

    /// <summary>1 活动优先 / 2 券优先（默认券优先）。</summary>
    [Column(Name = "priority")]
    public int Priority { get; set; } = MarketingPriorities.CouponFirst;
}

/// <summary>券类型。</summary>
public static class CouponTypes
{
    /// <summary>满减：门槛 + 减固定金额。</summary>
    public const int FullReduction = 1;

    /// <summary>折扣：门槛 × 折扣率。</summary>
    public const int Discount = 2;

    /// <summary>代金：减固定金额，可无门槛。</summary>
    public const int Cash = 3;

    /// <summary>满赠：达门槛送另一张券。</summary>
    public const int Gift = 4;
}

/// <summary>用户券状态。</summary>
public static class CouponStatuses
{
    /// <summary>未使用（券包里的可用券）。</summary>
    public const int Unused = 1;

    /// <summary>已占用：下单锁定的券。</summary>
    public const int Occupied = 2;

    /// <summary>已核销：支付成功，券作废。</summary>
    public const int Consumed = 3;

    /// <summary>已过期。</summary>
    public const int Expired = 4;
}

/// <summary>占券记录状态。</summary>
public static class OccupancyStatuses
{
    /// <summary>已占券（待支付）。</summary>
    public const int Occupied = 1;

    /// <summary>已核销（支付成功）。</summary>
    public const int Consumed = 2;

    /// <summary>已回退（取消 / 超时关单）。</summary>
    public const int Released = 3;
}

/// <summary>适用范围。</summary>
public static class TargetTypes
{
    /// <summary>全场。</summary>
    public const int All = 1;

    /// <summary>指定 SPU。</summary>
    public const int BySpu = 2;

    /// <summary>指定 SKU。</summary>
    public const int BySku = 3;
}

/// <summary>平台优惠优先级。</summary>
public static class MarketingPriorities
{
    /// <summary>活动优先：先取活动，无活动才取券。</summary>
    public const int ActivityFirst = 1;

    /// <summary>券优先：先取最优券，无券才取活动（默认）。</summary>
    public const int CouponFirst = 2;
}