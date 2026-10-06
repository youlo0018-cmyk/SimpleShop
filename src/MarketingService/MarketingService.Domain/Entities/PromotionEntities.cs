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

    /// <summary>满赠时每单赠送的券张数，1 ~ 100（DATA_SPEC 5.11）。</summary>
    /// <remarks>
    /// 非满赠活动忽略此值。少了它就只能一次送一张，
    /// 「满 500 送 3 张 20 元券」这类配置根本表达不出来。
    /// </remarks>
    [Column(Name = "gift_quantity")]
    public int GiftQuantity { get; set; } = 1;

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

    /// <summary>每单限购次数。<b>不在 DATA_SPEC 5.11 的字段表里，也不参与任何计算。</b></summary>
    /// <remarks>
    /// <para>活动的粒度是「一单命中一次」：优惠引擎对整单选出<b>一个</b>活动并按作用域行分摊
    /// （BUSINESS.md 11.2），所以任何大于等于 1 的取值与 1 完全等价 ——
    /// 这是个<b>没有第二种语义</b>的旋钮。既然它配了也不会改变任何行为，
    /// 就不该出现在活动表单上（「配了却不生效」比「没有这个配置」更糟）。</para>
    /// <para>列保留只为兼容历史数据；接口与表单都不再接受这个字段。</para>
    /// </remarks>
    [Column(Name = "per_order_limit")]
    public int PerOrderLimit { get; set; }

    /// <summary>总参与人次上限。<b>不在 DATA_SPEC 5.11 的字段表里，也不参与任何计算。</b></summary>
    /// <remarks>
    /// 活动的「限量」在规格里只有秒杀（<c>seckill_item.SeckillStock</c>）与券（模板池子）两种。
    /// 普通活动没有总量限制，所以这个字段与它旁边的 <see cref="UsedQuantity"/> 都不该出现在表单上。
    /// 列保留只为兼容历史数据；接口与表单都不再接受这两个字段。
    /// </remarks>
    [Column(Name = "total_quantity")]
    public int TotalQuantity { get; set; }

    /// <summary>已参与人次。<b>从未被写入过，也没有任何读取方</b>（见 <see cref="TotalQuantity"/>）。</summary>
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

/// <summary>满赠待发券记录（<c>gift_grant</c>）。</summary>
/// <remarks>
/// <para><b>为什么要有这张表</b>：满赠的「命中」发生在<b>下单试算</b>那一刻
/// （活动时间窗、行金额、活动配置都要按下单当时算），而券必须在<b>支付成功</b>时才发。
/// 两者之间隔着用户付款这段时间：期间活动可能被改、被停、甚至过期。
/// 支付时再重算一遍的话，用户会遇到「下单时显示送券、付完钱没有」——
/// 而页面全程没有任何报错。</para>
///
/// <para>所以下单试算时就把「这单该送什么」写死成一条待发记录，
/// 支付成功只负责按记录发券。<b>一条记录 = 一次发放承诺</b>，
/// 唯一索引 <c>(order_no, source_type, source_id)</c> 保证重复试算不会重复承诺。</para>
///
/// <para>发券是<b>支付收尾的一步</b>（在改订单状态之前），失败就让整笔支付重试：
/// 记录仍在「待发放」，重跑时因为状态判断与唯一索引都幂等，不会重复发。
/// 这样就不存在「付了钱、券没发、谁也不知道」的静默丢失。</para>
/// </remarks>
[Table(Name = "gift_grant")]
public class GiftGrant : EntityBase
{
    /// <summary>订单号。</summary>
    [Column(Name = "order_no", StringLength = 64)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>收券的客户 Id。</summary>
    [Column(Name = "customer_id")]
    public long CustomerId { get; set; }

    /// <summary>来源类型，见 <see cref="GiftGrantSources"/>。</summary>
    [Column(Name = "source_type")]
    public int SourceType { get; set; }

    /// <summary>来源 Id：活动 Id 或用户券 Id，随 <see cref="SourceType"/> 变化。</summary>
    [Column(Name = "source_id")]
    public long SourceId { get; set; }

    /// <summary>赠送的券模板 Id（下单时快照，之后活动改配置也不影响）。</summary>
    [Column(Name = "gift_template_id")]
    public long GiftTemplateId { get; set; }

    /// <summary>赠送张数。</summary>
    [Column(Name = "quantity")]
    public int Quantity { get; set; } = 1;

    /// <summary>状态，见 <see cref="GiftGrantStatuses"/>。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = GiftGrantStatuses.Pending;

    /// <summary>发放时间（UTC），未发放为空。</summary>
    [Column(Name = "issued_at")]
    public DateTime? IssuedAt { get; set; }
}

/// <summary>满赠待发券的来源。</summary>
public static class GiftGrantSources
{
    /// <summary>满赠活动（活动类型 3）。</summary>
    public const int Activity = 1;

    /// <summary>满赠券（券类型 4）。用户主动用了这张券，付完钱送它承诺的券。</summary>
    public const int Coupon = 2;
}

/// <summary>满赠待发券的状态。</summary>
public static class GiftGrantStatuses
{
    /// <summary>待发放：下单时已承诺，等支付成功。</summary>
    public const int Pending = 10;

    /// <summary>已发放：券已进用户券包。</summary>
    public const int Issued = 20;
}

/// <summary>活动参与记录（<c>marketing_activity_record</c>）。</summary>
/// <remarks>
/// <para><b>为什么要有这张表</b>：BUSINESS.md 17 要求活动报表给「参与订单数 / 参与金额 / 折扣总额」，
/// 并支持<b>下钻订单明细</b>。但订单行只存「这行减了多少钱」，<b>不存命中了哪个活动</b>，
/// 报表没法从订单侧反推。判定活动命中的地方只有一处 —— 下单试算 —— 所以在那儿记一笔。</para>
///
/// <para><b>活动名存快照</b>：活动可以改名甚至软删，历史报表要显示<b>当时</b>的名字。
/// 联表取当前值会让上个月的报表跟着这个月的改名一起变。</para>
///
/// <para><b>参与金额不在这张表里</b>：它必须按「已支付、未取消、未退款」算，
/// 那是订单服务的口径（与工作台 GMV 同源）。营销侧只记「哪些单参与了」，
/// 金额一律回订单服务问 —— 两处各算一遍必然对不上账。</para>
/// </remarks>
[Table(Name = "marketing_activity_record")]
public class MarketingActivityRecord : EntityBase
{
    /// <summary>订单号。</summary>
    [Column(Name = "order_no", StringLength = 64)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>下单的客户 Id。</summary>
    [Column(Name = "customer_id")]
    public long CustomerId { get; set; }

    /// <summary>命中的活动 Id。</summary>
    [Column(Name = "activity_id")]
    public long ActivityId { get; set; }

    /// <summary>活动名快照。</summary>
    [Column(Name = "activity_name", StringLength = 128)]
    public string ActivityName { get; set; } = string.Empty;

    /// <summary>归属平台 Id。</summary>
    [Column(Name = "platform_id")]
    public long PlatformId { get; set; }

    /// <summary>归属商户 Id，0 表示平台自营。</summary>
    [Column(Name = "merchant_id")]
    public long MerchantId { get; set; }

    /// <summary>本单因该活动实际让利多少，两位小数。</summary>
    [Column(Name = "discount_amount")]
    public decimal DiscountAmount { get; set; }
}
