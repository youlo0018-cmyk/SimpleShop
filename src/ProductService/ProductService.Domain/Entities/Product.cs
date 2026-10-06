using System.Linq.Expressions;
using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace ProductService.Domain.Entities;

// 金额字段一律声明为 decimal，精度 numeric(18,2) 由 deploy/sql/product 下的建表脚本定。
// 不要在 [Column] 上写 DecimalLength / Scale——FreeSql 的 ColumnAttribute 没有这两个属性，
// 写了会 CS0246。金额两位小数 + 四舍五入的规则见 DATA_SPEC「金额舍入口径」。

/// <summary>商品（SPU）。SKU 与规格都挂在它下面（DATA_SPEC 5.6、5.7）。</summary>
/// <remarks>
/// <b>AuditStatus 与 Status 是两件事，不要混</b>：
/// AuditStatus 管「审核有没有过」（10 待审核 / 20 已通过 / 30 已驳回），
/// Status 管「上架没有」（1 上架 / 2 下架）。审核通过不等于自动上架，
/// 上架还要显式操作，但上架的前置条件是审核必须已通过。
/// </remarks>
[Table(Name = "product")]
public class Product : AdminEntityBase, IPublicVisible<Product>
{
    /// <summary>构造该实体的公开可见条件（BUSINESS.md 1.4）。</summary>
    /// <param name="now">当前时间 UTC；商品不按时间窗判定，忽略。</param>
    /// <returns>「审核通过且已上架」的条件。</returns>
    /// <remarks>
    /// <para><b>为什么必须有这个方法</b>：BUSINESS.md 1.4 明确要求可见性过滤由 AOP 统一注入，
    /// 并写明「不靠每个 Handler 手写这些条件 —— 靠自觉写一定会漏」。
    /// 但在此之前**没有任何实体实现本接口**，于是 RegisterPublicVisibility 是空转的，
    /// 可见性完全落在各个 Handler 的手写 Where 上 —— 正是规格说要避免的那件事。</para>
    ///
    /// <para>只对 C 端 / 游客上下文生效（Admin 不注入，运营必须能看到待审核与下架商品）。
    /// 现在 shop 的几个 Handler 手写的条件与这里逐字一致，所以这是**加安全网**，
    /// 不改变任何现有行为；以后新加一个 C 端查询忘了写 Where，也不会把
    /// 未审核 / 已下架的商品漏给顾客。</para>
    /// </remarks>
    public Expression<Func<Product, bool>>? BuildPublicCondition(DateTime now)
        => x => x.AuditStatus == AuditStatuses.Approved && x.Status == ListingStatuses.OnShelf;

    /// <summary>商品名，2-128 字符。</summary>
    [Column(Name = "spu_name", StringLength = 128)]
    public string SpuName { get; set; } = string.Empty;

    /// <summary>副标题，≤ 200 字符。</summary>
    [Column(Name = "sub_title", StringLength = 200)]
    public string SubTitle { get; set; } = string.Empty;

    /// <summary>品牌 Id。0 = 未指定（品牌是选填项）。</summary>
    [Column(Name = "brand_id")]
    public long BrandId { get; set; }

    /// <summary>品牌名冗余。列表直接显示，不必二次查询（DATA_SPEC 4.4）。</summary>
    [Column(Name = "brand_name", StringLength = 64)]
    public string BrandName { get; set; } = string.Empty;

    /// <summary>分类 Id。<b>只能是第 3 级（叶子）分类</b>。</summary>
    [Column(Name = "category_id")]
    public long CategoryId { get; set; }

    /// <summary>分类名冗余。</summary>
    [Column(Name = "category_name", StringLength = 64)]
    public string CategoryName { get; set; } = string.Empty;

    /// <summary>配送方式。1 实物快递 / 2 虚拟商品 / 3 实物自提。决定运费、发货表单与退款窗口。</summary>
    [Column(Name = "delivery_type")]
    public int DeliveryType { get; set; } = DeliveryTypes.PhysicalExpress;

    /// <summary>主图 URL（上传框固定 180×180）。</summary>
    [Column(Name = "main_image", StringLength = 512)]
    public string MainImage { get; set; } = string.Empty;

    /// <summary>轮播图，JSON 数组字符串，≤ 6 张。</summary>
    [Column(Name = "images", StringLength = 2000)]
    public string Images { get; set; } = string.Empty;

    /// <summary>详情图，JSON 数组字符串，≤ 9 张。</summary>
    [Column(Name = "detail_images", StringLength = 2000)]
    public string DetailImages { get; set; } = string.Empty;

    /// <summary>划线原价。0 或 ≥ 所有 SKU 售价。精度 numeric(18,2) 由建表脚本定，实体只声明 decimal。</summary>
    [Column(Name = "original_price")]
    public decimal OriginalPrice { get; set; }

    /// <summary>最低售价，由所有启用 SKU 的售价取 min，服务端维护。</summary>
    [Column(Name = "min_price")]
    public decimal MinPrice { get; set; }

    /// <summary>最高售价，由所有启用 SKU 的售价取 max，服务端维护。</summary>
    [Column(Name = "max_price")]
    public decimal MaxPrice { get; set; }

    /// <summary>商品描述，≤ 4000 字符。</summary>
    [Column(Name = "description", StringLength = 4000)]
    public string Description { get; set; } = string.Empty;

    /// <summary>审核状态。10 待审核 / 20 已通过 / 30 已驳回。新建固定 10。</summary>
    [Column(Name = "audit_status")]
    public int AuditStatus { get; set; } = AuditStatuses.Pending;

    /// <summary>上架状态。1 上架 / 2 下架。新建默认下架。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = ListingStatuses.OffShelf;

    /// <summary>销量，支付成功累加。</summary>
    [Column(Name = "sales")]
    public long Sales { get; set; }

    /// <summary>
    /// 评价均分，首评星级均值，两位小数。
    /// </summary>
    /// <remarks>
    /// <b>冗余字段，C 端直接读它</b>，不实时去评价服务聚合：
    /// 商品列表页一次要展示几十个商品的评分，逐个调评价服务既慢又让列表强依赖评价服务可用性。
    /// 代价是它最多滞后 24 小时（每日 03:00 全量重算，见规格 14.5）。
    /// <b>0 表示还没有评价</b>，展示时用 <c>EvaluateCalculator.DisplayScore</c> 转成 5.0——
    /// 0 分会被用户理解成「很差」，而「还没人评价」是中性的。
    /// </remarks>
    [Column(Name = "evaluation_score")]
    public decimal EvaluationScore { get; set; }

    /// <summary>评价条数（只数首评，追评不计入）。</summary>
    [Column(Name = "evaluation_count")]
    public int EvaluationCount { get; set; }

    /// <summary>排序，小的在前。</summary>
    [Column(Name = "sort_order")]
    public int SortOrder { get; set; }

    /// <summary>备注，≤ 512 字符。</summary>
    [Column(Name = "remark", StringLength = 512)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>配送方式。</summary>
public static class DeliveryTypes
{
    /// <summary>实物快递：需要运费、需要物流、需要发货与收货。</summary>
    public const int PhysicalExpress = 1;

    /// <summary>虚拟商品：没有物流，支付后直接完成。</summary>
    public const int Virtual = 2;

    /// <summary>实物自提：需要取货码核销，不要物流信息。</summary>
    public const int PhysicalSelfPickup = 3;
}

/// <summary>商品审核状态。</summary>
public static class AuditStatuses
{
    /// <summary>待审核。新建商品的初始状态。</summary>
    public const int Pending = 10;

    /// <summary>已通过。<b>上架的前置条件</b>。</summary>
    public const int Approved = 20;

    /// <summary>已驳回。允许继续编辑，但编辑不重置审核状态，需重新提交。</summary>
    public const int Rejected = 30;
}

/// <summary>上下架状态。</summary>
public static class ListingStatuses
{
    /// <summary>上架。前台可见、可下单。</summary>
    public const int OnShelf = 1;

    /// <summary>下架。新建商品的默认状态。</summary>
    public const int OffShelf = 2;
}
