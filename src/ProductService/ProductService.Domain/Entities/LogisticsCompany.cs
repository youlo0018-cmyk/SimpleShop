using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace ProductService.Domain.Entities;

/// <summary>物流公司字典（DATA_SPEC 5.23）。发货表单里那个可搜索下拉的数据源。</summary>
/// <remarks>
/// <b>为什么要一张表而不是前端写死常量</b>：运营会新增快递公司（「极兔」是近几年才有的，
/// 「德邦」这类大件物流也不该由开发发版才能加）。写死在前端常量里的结果是
/// 每加一家公司都要改代码重新发布。
///
/// <para>属于字典数据而非订单数据，所以放在 ProductService（与 brand / category 同类），
/// 而不是 OrderService——发货单只记**名称快照**，不依赖这张表实时存在。</para>
///
/// <para>继承 <see cref="AdminEntityBase"/>：PlatformId 为 0 表示全平台共用，
/// 非 0 时只有该平台的账号能维护，走隐式租户过滤。</para>
/// </remarks>
[Table(Name = "logistics_company")]
public class LogisticsCompany : AdminEntityBase
{
    /// <summary>公司名称，如「顺丰速运」。同一平台内唯一（软删不占坑）。</summary>
    /// <remarks>发货单存的是**当时的名字快照**，所以这里改名不影响历史发货记录。</remarks>
    [Column(Name = "company_name", StringLength = 64)]
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>公司编码，如 <c>sf</c>。留空表示不填。</summary>
    /// <remarks>本项目只做展示，这个字段是为以后接快递鸟一类「按编码查单号」的接口预留的。</remarks>
    [Column(Name = "company_code", StringLength = 32)]
    public string CompanyCode { get; set; } = string.Empty;

    /// <summary>Logo URL，走 ToolService 上传。</summary>
    [Column(Name = "logo", StringLength = 512)]
    public string Logo { get; set; } = string.Empty;

    /// <summary>排序，小的在前。发货表单下拉按它排。</summary>
    [Column(Name = "sort_order")]
    public int SortOrder { get; set; }

    /// <summary>状态。1 启用 / 2 停用。</summary>
    /// <remarks>停用后不再出现在发货下拉里，<b>已发货的单不受影响</b>（发货单存的是名字快照）。</remarks>
    [Column(Name = "status")]
    public int Status { get; set; } = LogisticsCompanyStatuses.Enabled;

    /// <summary>备注。</summary>
    [Column(Name = "remark", StringLength = 512)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>物流公司状态。</summary>
public static class LogisticsCompanyStatuses
{
    /// <summary>启用。</summary>
    public const int Enabled = 1;

    /// <summary>停用。不再出现在下拉里，历史发货记录不受影响。</summary>
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
