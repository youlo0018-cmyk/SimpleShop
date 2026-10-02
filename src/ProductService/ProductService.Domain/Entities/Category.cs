using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace ProductService.Domain.Entities;

/// <summary>商品分类。强制最多三级（DATA_SPEC 5.4）。</summary>
/// <remarks>
/// <b>继承 AdminEntityBase</b>：分类有 PlatformId 归属（0 = 公共分类，全平台共用），
/// 且需要隐式租户过滤保证商户账号只能看到自己平台的私有分类。
/// Level 由服务端按 ParentId 链计算，前端不传——前端传了也一律忽略。
/// </remarks>
[Table(Name = "category")]
public class Category : AdminEntityBase
{
    /// <summary>上级分类 Id。0 = 一级分类。</summary>
    [Column(Name = "parent_id")]
    public long ParentId { get; set; }

    /// <summary>分类名称，同父级内唯一。</summary>
    [Column(Name = "category_name", StringLength = 64)]
    public string CategoryName { get; set; } = string.Empty;

    /// <summary>分类编码，全局唯一，可空。</summary>
    [Column(Name = "category_code", StringLength = 64)]
    public string CategoryCode { get; set; } = string.Empty;

    /// <summary>分类图标 URL（金刚区 / 分类页用）。</summary>
    [Column(Name = "icon", StringLength = 512)]
    public string Icon { get; set; } = string.Empty;

    /// <summary>分类大图 URL。</summary>
    [Column(Name = "image", StringLength = 512)]
    public string Image { get; set; } = string.Empty;

    /// <summary>排序，小的在前。</summary>
    [Column(Name = "sort_order")]
    public int SortOrder { get; set; }

    /// <summary>层级 1 / 2 / 3，由服务端计算。</summary>
    [Column(Name = "level")]
    public int Level { get; set; } = 1;

    /// <summary>状态。1 启用 / 2 停用。停用后前台不展示，已有商品不受影响。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = 1;
}