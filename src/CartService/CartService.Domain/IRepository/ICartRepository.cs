using CartService.Domain.Entities;

namespace CartService.Domain.IRepository;

/// <summary>购物车仓储。</summary>
public interface ICartRepository
{
    /// <summary>列出客户的全部购物车行。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>购物车行列表。幂等只读。</returns>
    Task<List<CartItem>> ListAsync(long customerId, CancellationToken ct = default);

    /// <summary>取某一行（按客户过滤，防止改别人的购物车）。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="cartId">购物车行 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>购物车行或 null。幂等只读。</returns>
    Task<CartItem?> GetAsync(long customerId, long cartId, CancellationToken ct = default);

    /// <summary>按 SKU 取一行。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>购物车行或 null。幂等只读。</returns>
    Task<CartItem?> GetBySkuAsync(long customerId, long skuId, CancellationToken ct = default);

    /// <summary>插入购物车行。</summary>
    /// <param name="item">购物车行，Id 与创建时间由仓储基类填。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新行 Id。非幂等。</returns>
    Task<long> InsertAsync(CartItem item, CancellationToken ct = default);

    /// <summary>按字段更新购物车行（数量 / 勾选 / 快照）。</summary>
    /// <param name="item">携带 Id 与待更新字段的实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> UpdateAsync(CartItem item, CancellationToken ct = default);

    /// <summary>软删购物车行。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="cartId">购物车行 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> SoftDeleteAsync(long customerId, long cartId, CancellationToken ct = default);

    /// <summary>清空某客户的购物车。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> ClearAsync(long customerId, CancellationToken ct = default);
}