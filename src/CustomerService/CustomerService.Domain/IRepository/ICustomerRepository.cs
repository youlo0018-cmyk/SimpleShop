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

    /// <summary>后台分页查客户。</summary>
    /// <param name="status">状态，0 表示不限。</param>
    /// <param name="keyword">按登录名 / 昵称 / 手机号模糊匹配。</param>
    /// <param name="page">页码。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当前页数据与总条数。</returns>
    /// <remarks>
    /// 手机号也进模糊匹配：运营手上只有一串手机号片段时，
    /// 让他先去别处查登录名是很反直觉的。
    /// </remarks>
    Task<(List<Customer> Items, long Total)> PageForAdminAsync(
        int status, string keyword, int page, int pageSize, CancellationToken ct = default);

    /// <summary>条件更新账号状态。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="expectedStatus">期望的当前状态（并发控制）。</param>
    /// <param name="newStatus">目标状态。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数；0 表示状态已被别人改过。</returns>
    /// <remarks>
    /// 带上期望的当前状态是**并发控制**：两个运营同时点「停用 / 启用」，
    /// 条件里带读到的原状态，只有一个能改成功，另一个拿到 0 就知道该跳过。
    /// 不带条件的话两个请求都会成功，而结果是「最后写的那次赢」——
    /// 界面显示的是另一个操作的结果，用户会以为系统出错了。
    /// </remarks>
    Task<int> TryChangeStatusAsync(
        long customerId, int expectedStatus, int newStatus, CancellationToken ct = default);
}

