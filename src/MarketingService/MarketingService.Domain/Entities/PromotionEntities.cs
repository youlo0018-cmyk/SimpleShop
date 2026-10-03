using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace MarketingService.Domain.Entities;

/// <summary>营销活动（满减 / 满折 / 满赠 / 限时抢购）。</summary>
/// <remarks>
/// <para>活动与券是<b>两个并列的优惠来源</b>，不是一套东西的两种叫法：
/// 活动不需要用户领取、直接作用于下单行；券要先领、订单级只能有一张。
/// BUSINESS.md 11.2 的「每个订单行只能命中 1 个活动或 1 张券」就是它们的关系——
/// <b>互斥</b>，同一行不能两个都吃到。</para>
///
/// <para>限时抢购（活动类型 4）共用这张表，靠 <see cref="SessionId"/> 区分场次，
/// 这样「多场次」的数据模型天然具备，不用等到做秒杀时再改表。</para>
/// </remarks>
[Table(Name = "promotion_activity")]
public class PromotionActivity : AdminEntityBase
{
    /// <summary>活动名，2-128 字符。</summary>
    [Column(Name = "activity_name", StringLength = 128)]
    public string ActivityName { get; set; } = string.Empty;

    /// <summary>活动类型，见 <see cref="ActivityTypes"/>。</summary>
    [Column(Name = "activity_type")]
    public int ActivityType { get; set; }

    /// <summary>门槛金额。0 表示无门槛。判定基数是<b>适用行金额合计</b>。</summary>
    [Column(Name = "threshold_amount")]
    public decimal ThresholdAmount { get; set; }

    /// <summary>优惠金额（满减）。</summary>
    [Column(Name = "discount_amount")]
    public decimal DiscountAmount { get; set; }

    /// <summary>折扣率数值，0.01 ~ 10。8.5 表示 85 折（满折用）。</summary>
    [Column(Name = "discount_rate")]
    public decimal DiscountRate { get; set; }

    /// <summary>满赠时赠送的券模板 Id。</summary>
    [Column(Name = "gift_template_id")]
    public long GiftTemplateId { get; set; }

    /// <summary>限时抢购场次 Id。非秒杀活动固定为 0。</summary>
    /// <remarks>
    /// 所有查询都按这个字段寻址，「当前场次」只是查询条件之一。
    /// 现在不建场次表也先把字段留出来，避免做秒杀时再改主表。
    /// </remarks>
    [Column(Name = "session_id")]
    public long SessionId { get; set; }

    /// <summary>适用范围类型，见 <see cref="TargetTypes"/>。</summary>
    [Column(Name = "target_type")]
    public int TargetType { get; set; } = TargetTypes.All;

    /// <summary>适用范围的 JSON 文本，结构随 <see cref="TargetType"/> 变化：BySpu 是 SPU Id 数组，BySku 是 SKU Id 数组。</summary>
    [Column(Name = "targets", StringLength = 4096)]
    public string Targets { get; set; } = "[]";

    /// <summary>开始时间（UTC）。</summary>
    [Column(Name = "start_time")]
    public DateTime StartTime { get; set; }

    /// <summary>结束时间（UTC）。</summary>
    [Column(Name = "end_time")]
    public DateTime EndTime { get; set; }

    /// <summary>每单限购次数，0 表示不限。</summary>
    [Column(Name = "per_order_limit")]
    public int PerOrderLimit { get; set; }

    /// <summary>总参与人次上限，0 表示不限。</summary>
    [Column(Name = "total_quantity")]
    public int TotalQuantity { get; set; }

    /// <summary>已参与人次。</summary>
    [Column(Name = "used_quantity")]
    public int UsedQuantity { get; set; }

    /// <summary>排序。值小的先参与「优惠力度相同」时的比较。</summary>
    [Column(Name = "sort_order")]
    public int SortOrder { get; set; }

    /// <summary>状态。1 启用 / 2 停用。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = 1;
}

/// <summary>活动类型。</summary>
public static class ActivityTypes
{
    /// <summary>满减：达门槛减固定金额。</summary>
    public const int FullReduction = 1;

    /// <summary>满折：达门槛按折扣率打折。</summary>
    public const int Discount = 2;

    /// <summary>满赠：达门槛送一张券，折扣额记 0。</summary>
    public const int Gift = 3;

    /// <summary>限时抢购：独立限量库存，单场次（数据模型已预留多场次）。</summary>
    public const int Seckill = 4;
}

/// <summary>活动的优惠来源标签，展示在商品卡的「优惠来源」角标上（DESIGN_SPEC 11.5）。</summary>
public static class DiscountSources
{
    /// <summary>没有优惠。</summary>
    public const string None = "none";

    /// <summary>活动。</summary>
    public const string Activity = "activity";

    /// <summary>券。</summary>
    public const string Coupon = "coupon";

    /// <summary>满赠：折扣额为 0，但仍是命中了活动，要显示「赠」。</summary>
    public const string Gift = "gift";
}