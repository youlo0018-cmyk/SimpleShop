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

    /// <inheritdoc />
    public async Task<(List<Customer> Items, long Total)> PageForAdminAsync(
        int status, string keyword, int page, int pageSize, CancellationToken ct = default)
    {
        var select = Db.Select<Customer>().Where(a => status <= 0 || a.Status == status);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            select = select.Where(a => a.CustomerName.Contains(kw)
                                   || a.NickName.Contains(kw)
                                   || a.Phone.Contains(kw));
        }

        var total = await select.CountAsync(ct).ConfigureAwait(false);

        // 按注册时间倒序：新客户在前，运营最常关心的是「刚来的那批」。
        // 同一时刻注册的用 Id 兜底，避免顺序抖动导致翻页时重复或漏行。
        var items = await select
            .OrderByDescending(a => a.CreatedAt).OrderByDescending(a => a.Id)
            .Page(page, pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<int> TryChangeStatusAsync(
        long customerId, int expectedStatus, int newStatus, CancellationToken ct = default)
        => await Db.Update<Customer>()
            .Where(a => a.Id == customerId && a.Status == expectedStatus)
            .Set(a => new Customer { Status = newStatus, UpdatedAt = DateTime.UtcNow })
            .ExecuteAffrowsAsync(ct)
            .ConfigureAwait(false);
}

