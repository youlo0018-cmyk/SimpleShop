using CustomerService.Domain.Entities;

namespace CustomerService.Domain.IRepository;

/// <summary>客户收藏仓储。单客户收藏上限 20（REVIEW.md P2 风险 20）。</summary>
public interface ICustomerFavoriteRepository
{
    /// <summary>分页查询收藏，按收藏时间倒序。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数，1-100。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>收藏列表与总数。幂等只读。</returns>
    Task<(List<CustomerFavorite> Items, long Total)> QueryPagedAsync(int page, int pageSize, CancellationToken ct = default);

    /// <summary>判断某商品是否已收藏。</summary>
    /// <param name="spuId">商品 SPU Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 表示已收藏。幂等只读。</returns>
    Task<bool> ExistsAsync(long spuId, CancellationToken ct = default);

    /// <summary>统计当前客户收藏总数，用于上限校验。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>收藏条数。幂等只读。</returns>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>插入收藏。</summary>
    /// <param name="favorite">收藏实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新收藏 Id。非幂等，重复收藏由业务层先查再拦。</returns>
    Task<long> InsertAsync(CustomerFavorite favorite, CancellationToken ct = default);

    /// <summary>按 SPU Id 软删收藏（取消收藏）。</summary>
    /// <param name="spuId">商品 SPU Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等：未收藏时返回 0。</returns>
    Task<int> DeleteBySpuAsync(long spuId, CancellationToken ct = default);
}

