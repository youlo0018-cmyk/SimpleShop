using Collaboration.Domain.Repository;
using FreeSql;
using UserService.Domain.Entities;
using UserService.Domain.IRepository;

namespace UserService.Infrastructure.Repository;

/// <summary>
/// 后台账号仓储实现。
/// </summary>
/// <remarks>
/// InsertAsync / UpdateAsync / GetByIdAsync 直接继承 CrudRepository——
/// 基类负责填雪花 Id 与审计时间戳，这里不要重复实现，否则会绕过 Id 填充
/// （踩过的坑：自己实现 InsertAsync 导致 Id 恒为 0，进而让唯一性校验的 excludeId 失效）。
/// 软删过滤由 GlobalFilter 注入；租户裁剪由调用方显式传参。
/// </remarks>
public sealed class UserRepository : CrudRepository<User>, IUserRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public UserRepository(IFreeSql freeSql) : base(freeSql)
    {
    }

    /// <inheritdoc />
    public async Task<User?> GetByNameAsync(string userName, CancellationToken ct = default)
        => await Db.Select<User>().Where(a => a.UserName == userName).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<List<User>> GetByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new List<User>();
        var list = ids.ToArray();
        return await Db.Select<User>().Where(a => list.Contains(a.Id)).ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByNameAsync(string userName, long excludeId = 0, CancellationToken ct = default)
        => await Db.Select<User>().Where(a => a.UserName == userName && a.Id != excludeId).AnyAsync(ct);

    /// <inheritdoc />
    public async Task<bool> ExistsByPhoneAsync(string phone, long excludeId = 0, CancellationToken ct = default)
        => await Db.Select<User>().Where(a => a.Phone == phone && a.Id != excludeId).AnyAsync(ct);

    /// <inheritdoc />
    public async Task<(List<User> Items, long Total)> QueryPagedAsync(
        int page, int pageSize, string keyword, long platformId, long merchantId, int status, CancellationToken ct = default)
    {
        var select = Db.Select<User>();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            select = select.Where(a => a.UserName.Contains(kw) || a.NickName.Contains(kw));
        }

        if (platformId > 0) select = select.Where(a => a.PlatformId == platformId);
        if (merchantId > 0) select = select.Where(a => a.MerchantId == merchantId);
        if (status > 0) select = select.Where(a => a.Status == status);

        var total = await select.CountAsync(ct);
        var items = await select.OrderByDescending(a => a.Id).Page(page, pageSize).ToListAsync(ct);
        return (items, total);
    }
}