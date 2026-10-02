using CustomerService.Domain.Entities;

namespace CustomerService.Domain.IRepository;

/// <summary>客户账号仓储。唯一性校验需要查库，所以放在 Handler 而不是 Validator（CODING_STANDARD 3.3）。</summary>
public interface ICustomerRepository
{
    /// <summary>按登录名取客户，不存在返回 null。</summary>
    /// <param name="customerName">登录名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>客户实体或 null。幂等只读，无副作用。</returns>
    Task<Customer?> GetByNameAsync(string customerName, CancellationToken ct = default);

    /// <summary>按 Id 取客户，不存在返回 null。</summary>
    /// <param name="id">客户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>客户实体或 null。幂等只读，无副作用。</returns>
    Task<Customer?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>判断登录名是否已被占用。</summary>
    /// <param name="customerName">登录名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 表示已存在。幂等只读，无副作用。</returns>
    Task<bool> ExistsByNameAsync(string customerName, CancellationToken ct = default);

    /// <summary>判断手机号是否已被占用。</summary>
    /// <param name="phone">手机号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 表示已存在。幂等只读，无副作用。</returns>
    Task<bool> ExistsByPhoneAsync(string phone, CancellationToken ct = default);

    /// <summary>插入客户。</summary>
    /// <param name="customer">客户实体，Id 与时间戳由 AOP 填充。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新客户的 Id。非幂等，重复调用会撞唯一索引。</returns>
    Task<long> InsertAsync(Customer customer, CancellationToken ct = default);

    /// <summary>按字段更新客户。</summary>
    /// <param name="customer">携带 Id 与待更新字段的实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等：重复提交相同值不改变结果。</returns>
    Task<int> UpdateAsync(Customer customer, CancellationToken ct = default);
}

