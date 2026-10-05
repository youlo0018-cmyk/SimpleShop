using Collaboration.Domain.Entities;
using Collaboration.Domain.Infrastructure;
using FreeSql.DataAnnotations;

namespace MerchantPlatformService.Domain.Entities;

/// <summary>平台（租户根）。</summary>
/// <remarks>
/// 继承 <see cref="AdminEntityBase"/> 而不是 <c>CustomerEntityBase</c>：
/// 平台与商户是**后台业务数据**，要按 platform_id / merchant_id 做租户隔离（DATA_SPEC 2.2）。
/// 本表的 <c>platform_id</c> / <c>merchant_id</c> 恒为 0——它自己就是租户根。
/// </remarks>
[Table(Name = "platform")]
public class Platform : AdminEntityBase, ITenantRoot
{
    /// <summary>平台名称，全局唯一（trim 后比较）。</summary>
    [Column(Name = "platform_name", StringLength = 128)]
    public string PlatformName { get; set; } = string.Empty;

    /// <summary>
    /// 平台编码，6 位字母，全局唯一。
    /// </summary>
    /// <remarks>
    /// <b>编辑时只读</b>：小程序端用 <c>PLATFORM_CODE</c> 锁死它，
    /// 改了等于让已发布的小程序找不到对应平台。
    /// 编辑保存时若传入值与原值不同，<b>忽略传入值</b>而不是报错——
    /// 后台表单常把 Code 一起提交回来，弹个错只会让运营以为自己改坏了。
    /// </remarks>
    [Column(Name = "platform_code", StringLength = 16)]
    public string PlatformCode { get; set; } = string.Empty;

    /// <summary>联系人。</summary>
    [Column(Name = "contact_name", StringLength = 64)]
    public string ContactName { get; set; } = string.Empty;

    /// <summary>联系电话，格式 <c>^1[3-9]\d{9}$</c>。</summary>
    [Column(Name = "contact_phone", StringLength = 20)]
    public string ContactPhone { get; set; } = string.Empty;

    /// <summary>平台 Logo（走 ToolService 上传）。</summary>
    [Column(Name = "logo", StringLength = 512)]
    public string Logo { get; set; } = string.Empty;

    /// <summary>商城名称，小程序顶部展示。</summary>
    [Column(Name = "mall_name", StringLength = 128)]
    public string MallName { get; set; } = string.Empty;

    /// <summary>首页公告。</summary>
    [Column(Name = "notice", StringLength = 500)]
    public string Notice { get; set; } = string.Empty;

    /// <summary>主题色，<c>#RRGGBB</c>。</summary>
    [Column(Name = "primary_color", StringLength = 16)]
    public string PrimaryColor { get; set; } = "#0071e3";

    /// <summary>TabBar 选中色，<c>#RRGGBB</c>。</summary>
    [Column(Name = "tab_color", StringLength = 16)]
    public string TabColor { get; set; } = "#0071e3";

    /// <summary>页面背景色，<c>#RRGGBB</c>。</summary>
    [Column(Name = "background_color", StringLength = 16)]
    public string BackgroundColor { get; set; } = "#f5f5f7";

    /// <summary>运费，<b>仅对实物快递收取</b>。两位小数。</summary>
    [Column(Name = "shipping_fee")]
    public decimal ShippingFee { get; set; }

    /// <summary>满额包邮门槛，按商品实付判定。<b>0 表示不启用包邮</b>。</summary>
    [Column(Name = "free_shipping_threshold")]
    public decimal FreeShippingThreshold { get; set; }

    /// <summary>状态，见 <see cref="PlatformStatuses"/>。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = PlatformStatuses.Enabled;

    /// <summary>备注。</summary>
    [Column(Name = "remark", StringLength = 512)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>平台状态。</summary>
public static class PlatformStatuses
{
    /// <summary>启用。</summary>
    public const int Enabled = 1;

    /// <summary>停用：小程序不可见、不可交易。<b>历史订单与报表不受影响</b>。</summary>
    public const int Disabled = 2;

    /// <summary>取中文名。</summary>
    /// <param name="status">状态值。</param>
    /// <returns>中文名，未知值返回「未知」。</returns>
    public static string NameOf(int status) => status switch
    {
        Enabled => "启用",
        Disabled => "停用",
        _ => "未知"
    };
}

/// <summary>商户 / 店铺。</summary>
[Table(Name = "merchant")]
public class Merchant : AdminEntityBase
{
    /// <summary>商户 / 店铺名称，<b>同平台内唯一</b>。</summary>
    [Column(Name = "merchant_name", StringLength = 128)]
    public string MerchantName { get; set; } = string.Empty;

    /// <summary>
    /// 商户编号，格式「平台编码 + 雪花 Id」，如 <c>DEMOPL13755080881608709</c>。
    /// </summary>
    /// <remarks>系统生成，用户不填。有唯一索引兜底。</remarks>
    [Column(Name = "merchant_no", StringLength = 64)]
    public string MerchantNo { get; set; } = string.Empty;

    /// <summary>联系人。</summary>
    [Column(Name = "contact_name", StringLength = 64)]
    public string ContactName { get; set; } = string.Empty;

    /// <summary>联系电话。</summary>
    [Column(Name = "contact_phone", StringLength = 20)]
    public string ContactPhone { get; set; } = string.Empty;

    /// <summary>店铺 Logo。</summary>
    [Column(Name = "logo", StringLength = 512)]
    public string Logo { get; set; } = string.Empty;

    /// <summary>店铺简介，C 端店铺页展示。</summary>
    [Column(Name = "description", StringLength = 1000)]
    public string Description { get; set; } = string.Empty;

    /// <summary>启停状态，见 <see cref="PlatformStatuses"/>。</summary>
    /// <remarks>
    /// <b>新建默认停用</b>：必须审核通过后才能运营。
    /// 停用后不可新增商品、不可接单，但历史订单与退款仍可处理；
    /// 副作用是<b>批量下架该商户全部已上架商品并同步搜索索引</b>。
    /// </remarks>
    [Column(Name = "status")]
    public int Status { get; set; } = PlatformStatuses.Disabled;

    /// <summary>审核状态，见 <see cref="MerchantAuditStatuses"/>。新建固定 10 待审核。</summary>
    [Column(Name = "audit_status")]
    public int AuditStatus { get; set; } = MerchantAuditStatuses.Pending;

    /// <summary>审核意见 / 拒绝原因。通过时选填，拒绝时必填。</summary>
    [Column(Name = "audit_remark", StringLength = 500)]
    public string AuditRemark { get; set; } = string.Empty;

    /// <summary>审核时间 UTC。未审核为 null。</summary>
    [Column(Name = "audited_at")]
    public DateTime? AuditedAt { get; set; }

    /// <summary>审核人 Id（后台账号）。</summary>
    [Column(Name = "auditor_id")]
    public long AuditorId { get; set; }

    /// <summary>审核人姓名快照。</summary>
    [Column(Name = "auditor_name", StringLength = 64)]
    public string AuditorName { get; set; } = string.Empty;

    /// <summary>
    /// 店铺评分（冗余，两位小数）。
    /// </summary>
    /// <remarks>
    /// <b>只统计有评价商品的均分平均值</b>；把零评价商品的默认 5.0 算进去会让评分虚高
    /// ——新店刷 10 个零评价商品，评分直接 5.0，比认真做生意的店还高（规格 14.5）。
    /// 0 表示还没有评价，展示时用 5.0。
    /// </remarks>
    [Column(Name = "rating")]
    public decimal Rating { get; set; }

    /// <summary>备注。</summary>
    [Column(Name = "remark", StringLength = 512)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>商户审核状态。</summary>
public static class MerchantAuditStatuses
{
    /// <summary>待审核。新建商户的初始状态。</summary>
    public const int Pending = 10;

    /// <summary>已通过。</summary>
    public const int Approved = 20;

    /// <summary>已拒绝。<b>拒绝要批量下架该商户全部已上架商品</b>。</summary>
    public const int Rejected = 90;

    /// <summary>取中文名。</summary>
    /// <param name="auditStatus">审核状态。</param>
    /// <returns>中文名，未知值返回「未知」。</returns>
    public static string NameOf(int auditStatus) => auditStatus switch
    {
        Pending => "待审核",
        Approved => "已通过",
        Rejected => "已拒绝",
        _ => "未知"
    };

    /// <summary>是否是可以提交的审核结论（只能 20 / 90）。</summary>
    /// <param name="auditStatus">审核状态。</param>
    /// <returns>返回 true 表示合法。</returns>
    public static bool IsConclusion(int auditStatus)
        => auditStatus is Approved or Rejected;
}
