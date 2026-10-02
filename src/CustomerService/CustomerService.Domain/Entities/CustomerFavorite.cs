using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace CustomerService.Domain.Entities;

/// <summary>客户收藏的商品（SPU）。单客户上限 20（REVIEW.md P2 风险 20）。</summary>
/// <remarks>刻意继承 CustomerEntityBase：AOP 的客户过滤按该基类判定，不继承就拿不到自动按 CustomerId 过滤。</remarks>
public class CustomerFavorite : CustomerEntityBase
{
    /// <summary>被收藏的商品 SPU Id。</summary>
    [Column(Name = "spu_id")]
    public long SpuId { get; set; }

    /// <summary>收藏时间，UTC。</summary>
    [Column(Name = "favorited_at")]
    public DateTime FavoritedAt { get; set; }
}

