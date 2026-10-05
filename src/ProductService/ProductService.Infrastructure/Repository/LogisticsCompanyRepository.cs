using Collaboration.Domain.Repository;
using FreeSql;
using ProductService.Domain.Entities;
using ProductService.Domain.IRepository;

namespace ProductService.Infrastructure.Repository;

/// <summary>
/// 物流公司字典仓储实现。
/// </summary>
/// <remarks>
/// 写入方法继承 <see cref="CrudRepository{T}"/>（雪花 Id 与审计时间戳由基类填）。
/// 查询不手写软删与租户过滤——那两件事由 FreeSql GlobalFilter 注入
/// （DATA_SPEC 3.2.1）。手写一遍的结果是「注册了 AOP 的服务查得到、没注册的查不到」，
/// 症状是同一个仓储在不同部署下结果不同。
/// </remarks>
public sealed class LogisticsCompanyRepository
    : CrudRepository<LogisticsCompany>, ILogisticsCompanyRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public LogisticsCompanyRepository(IFreeSql freeSql) : base(freeSql)
    {
    }

    /// <inheritdoc />
    public async Task<(List<LogisticsCompany> Items, long Total)> QueryPagedAsync(
        int page, int pageSize, string keyword, int status, CancellationToken ct = default)
    {
        var select = Db.Select<LogisticsCompany>()
            .Where(a => status <= 0 || a.Status == status);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            // 编码也一起搜：运营手里往往只有编码（如「yto」）而不是中文名。
            select = select.Where(a => a.CompanyName.Contains(kw) || a.CompanyCode.Contains(kw));
        }

        var total = await select.CountAsync(ct).ConfigureAwait(false);

        // 排序键是 SortOrder，再兜一个 Id 倒序。
        // 少了那个 Id：两家公司的 SortOrder 相同时顺序由数据库决定，
        // 翻页时同一行可能出现在第 1 页和第 2 页。
        var items = await select
            .OrderBy(a => a.SortOrder)
            .OrderByDescending(a => a.Id)
            .Page(page, pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<List<LogisticsCompany>> ListEnabledAsync(
        string keyword, CancellationToken ct = default)
    {
        var select = Db.Select<LogisticsCompany>()
            .Where(a => a.Status == LogisticsCompanyStatuses.Enabled);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            select = select.Where(a => a.CompanyName.Contains(kw) || a.CompanyCode.Contains(kw));
        }

        return await select
            .OrderBy(a => a.SortOrder)
            .OrderBy(a => a.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByNameAsync(
        string companyName, long excludeId = 0, CancellationToken ct = default)
        => await Db.Select<LogisticsCompany>()
            .Where(a => a.CompanyName == companyName && a.Id != excludeId)
            .AnyAsync(ct)
            .ConfigureAwait(false);
}
