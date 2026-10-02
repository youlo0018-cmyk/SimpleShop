using Collaboration.Domain.Repository;
using CustomerService.Domain.Entities;
using CustomerService.Domain.IRepository;
using FreeSql;

namespace CustomerService.Infrastructure.Repository;

/// <summary>客户收藏仓储实现。</summary>
public sealed class CustomerFavoriteRepository : CrudRepository<CustomerFavorite>, ICustomerFavoriteRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册 AOP 的 FreeSql 单例。</param>
    public CustomerFavoriteRepository(IFreeSql freeSql) : base(freeSql)
    {
    }

    /// <inheritdoc />
    public Task<(List<CustomerFavorite> Items, long Total)> QueryPagedAsync(int page, int pageSize, CancellationToken ct = default)
        => PageQueryAsync(page, pageSize, null, a => a.FavoritedAt, true, ct);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(long spuId, CancellationToken ct = default)
        => Db.Select<CustomerFavorite>().Where(a => a.SpuId == spuId).AnyAsync(ct);

    /// <summary>统计当前客户收藏总数。基类的谓词参数对本场景恒为 null，这里做一次显式适配。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>收藏条数。幂等只读。</returns>
    Task<long> ICustomerFavoriteRepository.CountAsync(CancellationToken ct) => CountAsync(null, ct);

    /// <inheritdoc />
    public Task<int> DeleteBySpuAsync(long spuId, CancellationToken ct = default)
        => Db.Update<CustomerFavorite>()
            .Where(a => a.SpuId == spuId)
            .Set(a => new CustomerFavorite { IsDeleted = true, DeletedAt = DateTime.UtcNow })
            .ExecuteAffrowsAsync(ct);
}

