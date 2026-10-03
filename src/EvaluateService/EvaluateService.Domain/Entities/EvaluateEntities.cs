using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace EvaluateService.Domain.Entities;

/// <summary>
/// 评价（首评）。粒度是 <b>SPU 级</b>，一条订单内同一 SPU 只能有一条。
/// </summary>
/// <remarks>
/// <para><b>为什么基类选 <see cref="CustomerEntityBase"/> 而不是 <see cref="AdminEntityBase"/></b>：
/// 评价是客户写的数据，C 端「我的评价」必须自动按 <c>customer_id</c> 过滤，
/// 这正是客户 AOP 的职责。但后台又要按商户看评价，所以另外冗余
/// <c>platform_id</c> / <c>merchant_id</c> 两个字段做**查询维度**（不参与 AOP 隔离）。
/// 依据：DATA_SPEC.md 2.3、BUSINESS.md 14。</para>
///
/// <para><b>为什么单独存 <c>spu_name</c> 快照</b>：商品改名 / 下架 / 删除后评价仍要展示，
/// 联表会跟着变，历史评价就「改口」了。规格 14.1 明确要求「评价保留」。</para>
/// </remarks>
[Table(Name = "evaluate")]
public class Evaluate : CustomerEntityBase
{
    /// <summary>所属平台 Id，冗余自商品，仅作后台查询维度。</summary>
    [Column(Name = "platform_id")]
    public long PlatformId { get; set; }

    /// <summary>所属商户 Id，冗余自商品，仅作后台查询维度。</summary>
    [Column(Name = "merchant_id")]
    public long MerchantId { get; set; }

    /// <summary>商品（SPU）Id。</summary>
    [Column(Name = "spu_id")]
    public long SpuId { get; set; }

    /// <summary>商品名快照。商品改名后评价不改口。</summary>
    [Column(Name = "spu_name", StringLength = 128)]
    public string SpuName { get; set; } = string.Empty;

    /// <summary>
    /// 本订单实际购买的规格名清单快照，逗号分隔。
    /// </summary>
    /// <remarks>
    /// 存快照而不是联 SKU 表：SKU 可能被删 / 改规格，历史评价展示的必须是当时买的是什么。
    /// 规格最多列 3 个，超出追加「等 N 个规格」（规格 14.1）。
    /// </remarks>
    [Column(Name = "sku_specs", StringLength = 512)]
    public string SkuSpecs { get; set; } = string.Empty;

    /// <summary>来源订单号。幂等键的一部分。</summary>
    [Column(Name = "order_no", StringLength = 64)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>订单 Id，便于后台反查订单详情。</summary>
    [Column(Name = "order_id")]
    public long OrderId { get; set; }

    /// <summary>星级，1~5 的整数，必选。</summary>
    [Column(Name = "star_score")]
    public int StarScore { get; set; } = 5;

    /// <summary>评价文字，可空（配图也算一条有效评价）。</summary>
    [Column(Name = "content", StringLength = 1000)]
    public string Content { get; set; } = string.Empty;

    /// <summary>图片 URL 列表，英文逗号分隔，最多 9 张。</summary>
    [Column(Name = "images", StringLength = 2048)]
    public string Images { get; set; } = string.Empty;

    /// <summary>是否匿名。匿名时 C 端显示「匿名用户」，后台可见真实昵称。</summary>
    [Column(Name = "is_anonymous")]
    public bool IsAnonymous { get; set; }

    /// <summary>是否被后台隐藏。隐藏后 C 端不展示，但数据保留。</summary>
    [Column(Name = "is_hidden")]
    public bool IsHidden { get; set; }

    /// <summary>隐藏原因，后台可见，C 端不可见。</summary>
    [Column(Name = "hidden_reason", StringLength = 500)]
    public string HiddenReason { get; set; } = string.Empty;

    /// <summary>隐藏时间 UTC。未隐藏为 null。</summary>
    [Column(Name = "hidden_at")]
    public DateTime? HiddenAt { get; set; }

    /// <summary>隐藏操作人 Id（后台账号）。</summary>
    [Column(Name = "hidden_by_id")]
    public long HiddenById { get; set; }
}

/// <summary>评价的 SKU 标记。一条评价按其覆盖的每个 SKU 各记一条。</summary>
/// <remarks>
/// 这张表存在的唯一理由是规格 14.1 的「详情页按当前选中 SKU 过滤」：
/// 「红色」的评价不该出现在只看「蓝色」的用户面前。
/// <c>sku_spec_text</c> 存快照，与 <see cref="Evaluate.SkuSpecs"/> 互为冗余，
/// 单独存是为了按 SKU 查询时能顺带取到规格文本，不用再回查主表。
/// </remarks>
[Table(Name = "evaluate_sku_ref")]
public class EvaluateSkuRef : EntityBase
{
    /// <summary>评价 Id。</summary>
    [Column(Name = "evaluate_id")]
    public long EvaluateId { get; set; }

    /// <summary>被标记的 SKU Id。</summary>
    [Column(Name = "sku_id")]
    public long SkuId { get; set; }

    /// <summary>规格文本快照，如「红色 / M」。</summary>
    [Column(Name = "sku_spec_text", StringLength = 256)]
    public string SkuSpecText { get; set; } = string.Empty;

    /// <summary>
    /// 对应的订单行 Id。
    /// </summary>
    /// <remarks>
    /// 保留它是为了能反查「这条评价对应订单里哪一行」，
    /// 以及后台核对时能对上原始下单金额。
    /// </remarks>
    [Column(Name = "order_item_id")]
    public long OrderItemId { get; set; }
}

/// <summary>追评。挂在首评下方，最多 3 条。</summary>
/// <remarks>
/// <b>追评不单独计入商品均分</b>（规格 14.2），防止「先打 5 星再追评 1 星」刷分。
/// 因此聚合分只统计 <see cref="Evaluate"/>，不统计本表。
/// 追评与首评标记同一组 SKU，所以不重复存 SKU 标记。
/// </remarks>
[Table(Name = "evaluate_append")]
public class EvaluateAppend : CustomerEntityBase
{
    /// <summary>所属首评 Id。</summary>
    [Column(Name = "evaluate_id")]
    public long EvaluateId { get; set; }

    /// <summary>追评内容，可空（配图也算）。</summary>
    [Column(Name = "content", StringLength = 1000)]
    public string Content { get; set; } = string.Empty;

    /// <summary>追评图片 URL 列表，逗号分隔，最多 9 张。</summary>
    [Column(Name = "images", StringLength = 2048)]
    public string Images { get; set; } = string.Empty;

    /// <summary>
    /// 追评星级，0 表示不打分。
    /// </summary>
    /// <remarks>
    /// 存了但不参与均分（规格 14.2），只用于展示「追评时改成了几星」。
    /// </remarks>
    [Column(Name = "star_score")]
    public int StarScore { get; set; }
}

/// <summary>评价回复。商户与平台各可回复 1 次。</summary>
/// <remarks>
/// <c>append_id = 0</c> 表示回复首评，非 0 表示回复某条追评。
/// 回复**不可编辑**只能追加（规格 14.3），所以这里只有新增没有更新入口。
/// </remarks>
[Table(Name = "evaluate_reply")]
public class EvaluateReply : EntityBase
{
    /// <summary>所属首评 Id。</summary>
    [Column(Name = "evaluate_id")]
    public long EvaluateId { get; set; }

    /// <summary>被回复的追评 Id，0 表示回复首评。</summary>
    [Column(Name = "append_id")]
    public long AppendId { get; set; }

    /// <summary>回复内容。</summary>
    [Column(Name = "content", StringLength = 1000)]
    public string Content { get; set; } = string.Empty;

    /// <summary>回复主体，见 <see cref="EvaluateReplyTypes"/>。</summary>
    [Column(Name = "reply_type")]
    public int ReplyType { get; set; }

    /// <summary>回复人 Id（后台账号）。</summary>
    [Column(Name = "reply_by_id")]
    public long ReplyById { get; set; }

    /// <summary>回复人姓名快照。</summary>
    [Column(Name = "reply_by_name", StringLength = 64)]
    public string ReplyByName { get; set; } = string.Empty;
}

/// <summary>回复主体。</summary>
public static class EvaluateReplyTypes
{
    /// <summary>商户回复。</summary>
    public const int Merchant = 1;

    /// <summary>平台回复。</summary>
    public const int Platform = 2;

    /// <summary>取中文名。</summary>
    /// <param name="replyType">回复主体。</param>
    /// <returns>中文名，未知值返回「未知」。</returns>
    public static string NameOf(int replyType) => replyType switch
    {
        Merchant => "商户回复",
        Platform => "平台回复",
        _ => "未知"
    };
}
