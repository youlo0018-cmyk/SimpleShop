using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace ProductService.Domain.Entities;

/// <summary>商品品牌。商品的 BrandId 是**选填项**，所以品牌表可以为空（DATA_SPEC 5.5）。</summary>
[Table(Name = "brand")]
public class Brand : AdminEntityBase
{
    /// <summary>品牌名，全局唯一。</summary>
    [Column(Name = "brand_name", StringLength = 64)]
    public string BrandName { get; set; } = string.Empty;

    /// <summary>品牌编码，选填；填写时全局唯一。</summary>
    [Column(Name = "brand_code", StringLength = 64)]
    public string BrandCode { get; set; } = string.Empty;

    /// <summary>品牌 Logo URL。</summary>
    [Column(Name = "logo", StringLength = 512)]
    public string Logo { get; set; } = string.Empty;

    /// <summary>排序，小的在前。</summary>
    [Column(Name = "sort_order")]
    public int SortOrder { get; set; }

    /// <summary>状态。1 启用 / 2 停用。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = 1;
}
