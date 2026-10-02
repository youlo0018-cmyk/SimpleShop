using Collaboration.Domain.Repository;
using CustomerService.Domain.Entities;
using CustomerService.Domain.IRepository;
using FreeSql;

namespace CustomerService.Infrastructure.Repository;

/// <summary>客户账号仓储实现。软删与可见性条件由 AOP 注入，这里不重复写。</summary>
public sealed class CustomerRepository : CrudRepository<Customer>, ICustomerRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册 AOP 的 FreeSql 单例。</param>
    public CustomerRepository(IFreeSql freeSql) : base(freeSql)
    {
    }

    /// <inheritdoc />
    public async Task<Customer?> GetByNameAsync(string customerName, CancellationToken ct = default)
        => await Db.Select<Customer>().Where(a => a.CustomerName == customerName).FirstAsync(ct);

    /// <inheritdoc />
    public Task<bool> ExistsByNameAsync(string customerName, CancellationToken ct = default)
        => Db.Select<Customer>().Where(a => a.CustomerName == customerName).AnyAsync(ct);

    /// <inheritdoc />
    public Task<bool> ExistsByPhoneAsync(string phone, CancellationToken ct = default)
        => Db.Select<Customer>().Where(a => a.Phone == phone).AnyAsync(ct);
}

