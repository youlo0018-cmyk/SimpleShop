namespace UserService.Domain.IRepository;

/// <summary>后台账号仓储。</summary>
public interface IUserRepository
{
    /// <summary>按登录名取账号（含软删过滤）。</summary>
    /// <param name="userName">登录名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>账号或 null。幂等只读。</returns>
    Task<Entities.User?> GetByNameAsync(string userName, CancellationToken ct = default);

    /// <summary>按 Id 取账号。</summary>
    /// <param name="id">账号 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>账号或 null。幂等只读。</returns>
    Task<Entities.User?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>按 Id 集合取账号。</summary>
    /// <param name="ids">账号 Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>账号列表。幂等只读。</returns>
    Task<List<Entities.User>> GetByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// <summary>判断登录名是否已被占用。</summary>
    /// <param name="userName">登录名。</param>
    /// <param name="excludeId">排除的 Id（编辑时传自身），可为 0。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>存在返回 true。幂等只读。</returns>
    Task<bool> ExistsByNameAsync(string userName, long excludeId = 0, CancellationToken ct = default);

    /// <summary>判断手机号是否已被占用。</summary>
    /// <param name="phone">手机号。</param>
    /// <param name="excludeId">排除的 Id，可为 0。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>存在返回 true。幂等只读。</returns>
    Task<bool> ExistsByPhoneAsync(string phone, long excludeId = 0, CancellationToken ct = default);

    /// <summary>分页查询账号，按平台 / 商户 / 状态 / 关键字裁剪。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数，1-100。</param>
    /// <param name="keyword">按登录名或昵称模糊搜索。</param>
    /// <param name="platformId">平台 Id，0 表示不限（仅超管可用）。</param>
    /// <param name="merchantId">商户 Id，0 表示不限。</param>
    /// <param name="status">状态，0 表示不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>账号列表与总数。</returns>
    Task<(List<Entities.User> Items, long Total)> QueryPagedAsync(
        int page, int pageSize, string keyword, long platformId, long merchantId, int status, CancellationToken ct = default);

    /// <summary>插入账号。</summary>
    /// <param name="user">账号实体，Id 与审计字段由 AOP 填充。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新账号 Id。非幂等。</returns>
    Task<long> InsertAsync(Entities.User user, CancellationToken ct = default);

    /// <summary>按字段更新账号。</summary>
    /// <param name="user">携带 Id 与待更新字段的实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> UpdateAsync(Entities.User user, CancellationToken ct = default);
}