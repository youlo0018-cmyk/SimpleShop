using CartService.Domain.Entities;
using CartService.Domain.IRepository;
using Collaboration.Domain.Repository;
using FreeSql;

namespace CartService.Infrastructure.Repository;

/// <summary>购物车仓储实现。写入方法继承 CrudRepository（雪花 Id 与时间戳由基类填）。</summary>
public sealed class CartRepository : CrudRepository<CartItem>, ICartRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public CartRepository(IFreeSql freeSql) : base(freeSql) { }

    /// <inheritdoc />
    public async Task<List<CartItem>> ListAsync(long customerId, CancellationToken ct = default)
        => await Db.Select<CartItem>()
            .Where(a => a.CustomerId == customerId)
            .OrderByDescending(a => a.UpdatedAt)
            .OrderByDescending(a => a.Id)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<CartItem?> GetAsync(long customerId, long cartId, CancellationToken ct = default)
        => await Db.Select<CartItem>().Where(a => a.Id == cartId && a.CustomerId == customerId).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<CartItem?> GetBySkuAsync(long customerId, long skuId, CancellationToken ct = default)
        => await Db.Select<CartItem>().Where(a => a.CustomerId == customerId && a.SkuId == skuId).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<int> SoftDeleteAsync(long customerId, long cartId, CancellationToken ct = default)
        => await Db.Update<CartItem>()
            .Where(a => a.Id == cartId && a.CustomerId == customerId)
            .Set(a => new CartItem { IsDeleted = true, DeletedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow })
            .ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public async Task<int> ClearAsync(long customerId, CancellationToken ct = default)
        => await Db.Update<CartItem>()
            .Where(a => a.CustomerId == customerId)
            .Set(a => new CartItem { IsDeleted = true, DeletedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow })
            .ExecuteAffrowsAsync(ct);
}