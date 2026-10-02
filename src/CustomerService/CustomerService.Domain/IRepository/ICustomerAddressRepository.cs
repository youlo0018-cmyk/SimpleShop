using CustomerService.Domain.Entities;

namespace CustomerService.Domain.IRepository;

/// <summary>客户收货地址仓储。所有查询由 AOP 自动按 CustomerId 过滤（DATA_SPEC 2.3）。</summary>
public interface ICustomerAddressRepository
{
    /// <summary>分页查询当前客户的地址，默认地址优先。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数，1-100。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>地址列表与总数。幂等只读。</returns>
    Task<(List<CustomerAddress> Items, long Total)> QueryPagedAsync(int page, int pageSize, CancellationToken ct = default);

    /// <summary>按 Id 取地址，不存在返回 null。</summary>
    /// <param name="id">地址 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>地址实体或 null。幂等只读。</returns>
    Task<CustomerAddress?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>插入地址。</summary>
    /// <param name="address">地址实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新地址 Id。非幂等。</returns>
    Task<long> InsertAsync(CustomerAddress address, CancellationToken ct = default);

    /// <summary>按字段更新地址。</summary>
    /// <param name="address">携带 Id 与待更新字段的实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> UpdateAsync(CustomerAddress address, CancellationToken ct = default);

    /// <summary>把该客户下所有地址的 IsDefault 置 false，再把目标地址置 true。</summary>
    /// <param name="targetId">要设为默认的地址 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等：重复设置为同一地址不产生额外变更。</returns>
    Task<int> SetDefaultAsync(long targetId, CancellationToken ct = default);

    /// <summary>软删地址。</summary>
    /// <param name="id">地址 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等：重复软删第二次返回 0。</returns>
    Task<int> DeleteAsync(long id, CancellationToken ct = default);
}

