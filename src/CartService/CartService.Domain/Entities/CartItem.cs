using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace CartService.Domain.Entities;

/// <summary>购物车行。一个客户对一个 SKU 只有一行。</summary>
/// <remarks>
/// <b>Add 是累加语义</b>（BUSINESS.md 8.3）：调用方传的是<b>增量</b>，不是目标数量。
/// 传目标数量会出现倍数增长——原本 3 件、传「3」之后变成 6，这是很容易犯且很难发现的错。
///
/// 快照字段（sku_name / sku_spec_text / price / image）在每次加购时刷新：
/// 商品改名或改价后购物车要立刻反映最新的，否则结算页显示的价格和实际下单的不一致。
/// </remarks>
[Table(Name = "cart_item")]
public class CartItem : EntityBase
{
    /// <summary>客户 Id。</summary>
    [Column(Name = "customer_id")]
    public long CustomerId { get; set; }

    /// <summary>SKU Id。</summary>
    [Column(Name = "sku_id")]
    public long SkuId { get; set; }

    /// <summary>所属商品 Id。</summary>
    [Column(Name = "product_id")]
    public long ProductId { get; set; }

    /// <summary>数量，1 ~ 99。</summary>
    [Column(Name = "quantity")]
    public int Quantity { get; set; } = 1;

    /// <summary>SKU 名快照（商品名 + 规格值）。</summary>
    [Column(Name = "sku_name", StringLength = 256)]
    public string SkuName { get; set; } = string.Empty;

    /// <summary>规格文本快照，如「红色 / M」。</summary>
    [Column(Name = "sku_spec_text", StringLength = 256)]
    public string SkuSpecText { get; set; } = string.Empty;

    /// <summary>单价快照。</summary>
    [Column(Name = "price")]
    public decimal Price { get; set; }

    /// <summary>划线原价快照。</summary>
    [Column(Name = "original_price")]
    public decimal OriginalPrice { get; set; }

    /// <summary>图片快照。加购时刷新（BUSINESS.md 8.3）。</summary>
    [Column(Name = "image", StringLength = 512)]
    public string Image { get; set; } = string.Empty;

    /// <summary>结算页勾选状态，默认勾选。</summary>
    [Column(Name = "checked")]
    public bool Checked { get; set; } = true;
}

/// <summary>购物车数量上限（BUSINESS.md 8.3：累计上限 99）。</summary>
public static class CartRules
{
    /// <summary>单个 SKU 的数量上限。</summary>
    public const int MaxQuantity = 99;
}