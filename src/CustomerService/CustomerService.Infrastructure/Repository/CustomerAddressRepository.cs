using Collaboration.Domain.Repository;
using CustomerService.Domain.Entities;
using CustomerService.Domain.IRepository;
using FreeSql;

namespace CustomerService.Infrastructure.Repository;

/// <summary>客户地址仓储实现。</summary>
public sealed class CustomerAddressRepository : CrudRepository<CustomerAddress>, ICustomerAddressRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册 AOP 的 FreeSql 单例。</param>
    public CustomerAddressRepository(IFreeSql freeSql) : base(freeSql)
    {
    }

    /// <inheritdoc />
    public Task<(List<CustomerAddress> Items, long Total)> QueryPagedAsync(int page, int pageSize, CancellationToken ct = default)
        => PageQueryAsync(page, pageSize, null, a => a.IsDefault, true, ct);

    /// <inheritdoc />
    public Task<int> SetDefaultAsync(long targetId, CancellationToken ct = default)
    {
        // 两条语句必须原子：先全清再置目标，否则中间态会撞「同一客户至多一条默认」的部分唯一索引，
        // 或者出现两条默认地址。用本地事务包住（DATA_SPEC 3.7：单服务内多表写入必须事务）。
        var affected = 0;

        // FreeSql 3.5 的 IFreeSql.Transaction 只接受同步委托，事务体里用同步的 ExecuteAffrows。
        Db.Transaction(() =>
        {
            Db.Update<CustomerAddress>()
                .Where(a => a.IsDefault && a.Id != targetId)
                .Set(a => new CustomerAddress { IsDefault = false })
                .ExecuteAffrows();
            affected = Db.Update<CustomerAddress>()
                .Where(a => a.Id == targetId)
                .Set(a => new CustomerAddress { IsDefault = true })
                .ExecuteAffrows();
        });

        return Task.FromResult(affected);
    }
}

