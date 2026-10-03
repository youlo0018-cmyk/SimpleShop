using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace ProductService.Domain.Entities;

/// <summary>规格项（如「颜色」「尺码」）。SPU 下动态定义，**不建全局规格字典**（DATA_SPEC 5.7.1）。</summary>
/// <remarks>
/// 不建全局字典的理由：手机是「颜色+容量」，食品是「规格+口味」，字典表维护成本高且容易脏。
/// 前端按该 SPU 的规格项动态渲染选择器。
/// </remarks>
[Table(Name = "product_spec")]
public class ProductSpec : EntityBase
{
    /// <summary>所属商品 Id。</summary>
    [Column(Name = "product_id")]
    public long ProductId { get; set; }

    /// <summary>规格项名，1-32 字符，同 SPU 内不重复。</summary>
    [Column(Name = "spec_name", StringLength = 32)]
    public string SpecName { get; set; } = string.Empty;

    /// <summary>排序，小的在前。<b>SKU 的 SkuSpecText 按这个顺序拼接</b>，所以顺序影响展示。</summary>
    [Column(Name = "sort_order")]
    public int SortOrder { get; set; }
}

/// <summary>规格值（如「红色」「M」）。</summary>
[Table(Name = "product_spec_value")]
public class ProductSpecValue : EntityBase
{
    /// <summary>所属规格项 Id。</summary>
    [Column(Name = "spec_id")]
    public long SpecId { get; set; }

    /// <summary>所属商品 Id。冗余一份，省得每次都回表查规格项。</summary>
    [Column(Name = "product_id")]
    public long ProductId { get; set; }

    /// <summary>规格值名，1-32 字符，同规格项内不重复。</summary>
    [Column(Name = "value_name", StringLength = 32)]
    public string ValueName { get; set; } = string.Empty;

    /// <summary>排序，小的在前。</summary>
    [Column(Name = "sort_order")]
    public int SortOrder { get; set; }
}

/// <summary>SKU（库存单元）。</summary>
/// <remarks>
/// <b>这里刻意没有 Stock 字段。</b>库存由 InventoryService 持有，在
/// <c>simpleshopinventory</c> 库里；商品表里再存一份库存就成了两份真相，
/// 迟早对不上。创建商品时传的 Stock 只是<b>初始化值</b>，用完即弃；
/// 之后的锁定 / 扣减 / 释放 / 回补全部由 InventoryService 驱动，
/// 商品编辑页也不再改库存（DATA_SPEC 5.7.2）。
/// </remarks>
[Table(Name = "sku")]
public class Sku : EntityBase
{
    /// <summary>所属商品 Id。</summary>
    [Column(Name = "product_id")]
    public long ProductId { get; set; }

    /// <summary>SKU 编码，全局唯一，<b>按编码 Upsert</b>。</summary>
    [Column(Name = "sku_code", StringLength = 64)]
    public string SkuCode { get; set; } = string.Empty;

    /// <summary>SKU 名 = 商品名 + 各规格值按规格项顺序拼接，服务端生成。</summary>
    [Column(Name = "sku_name", StringLength = 256)]
    public string SkuName { get; set; } = string.Empty;

    /// <summary>规格组合文案，如「红色 / M」，列表直接显示，不必回表拼。</summary>
    [Column(Name = "sku_spec_text", StringLength = 256)]
    public string SkuSpecText { get; set; } = string.Empty;

    /// <summary>售价，&gt; 0，两位小数。</summary>
    [Column(Name = "price")]
    public decimal Price { get; set; }

    /// <summary>划线原价。0 或 ≥ Price。</summary>
    [Column(Name = "original_price")]
    public decimal OriginalPrice { get; set; }

    /// <summary>SKU 图 URL。</summary>
    [Column(Name = "image", StringLength = 512)]
    public string Image { get; set; } = string.Empty;

    /// <summary>状态，见 <see cref="SkuStatuses"/>。停用后不可下单。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = SkuStatuses.Enabled;

}

/// <summary>SKU 状态。</summary>
public static class SkuStatuses
{
    /// <summary>启用。前台可见、可下单。</summary>
    public const int Enabled = 1;

    /// <summary>停用。不可下单，但历史订单里仍然显示（订单行是快照，不受影响）。</summary>
    public const int Disabled = 2;
}

/// <summary>SKU 与规格值的关联（多对多）。</summary>
/// <remarks>
/// 复合主键 (sku_id, spec_value_id)。一个 SKU 必须<b>覆盖该 SPU 的所有规格项</b>，
/// 这条规则由应用层校验（DATA_SPEC 5.7.2）。
/// </remarks>
[Table(Name = "sku_spec_value")]
public class SkuSpecValue
{
    /// <summary>SKU Id。</summary>
    [Column(Name = "sku_id", IsPrimary = true)]
    public long SkuId { get; set; }

    /// <summary>规格值 Id。</summary>
    [Column(Name = "spec_value_id", IsPrimary = true)]
    public long SpecValueId { get; set; }

    /// <summary>规格项 Id。冗余一份，校验「覆盖所有规格项」时不必多一次回表。</summary>
    [Column(Name = "spec_id")]
    public long SpecId { get; set; }

    /// <summary>创建时间，UTC。</summary>
    [Column(Name = "created_at")]
    public DateTime CreatedAt { get; set; }
}