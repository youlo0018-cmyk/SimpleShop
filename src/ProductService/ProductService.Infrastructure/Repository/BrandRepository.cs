using Collaboration.Domain.Repository;
using FreeSql;
using ProductService.Domain.Entities;
using ProductService.Domain.IRepository;

namespace ProductService.Infrastructure.Repository;

/// <summary>品牌仓储实现。写入方法继承 CrudRepository（雪花 Id 与审计时间戳由基类填）。</summary>
public sealed class BrandRepository : CrudRepository<Brand>, IBrandRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public BrandRepository(IFreeSql freeSql) : base(freeSql)
    {
    }

    /// <inheritdoc />
    public async Task<(List<Brand> Items, long Total)> QueryPagedAsync(
        int page, int pageSize, string keyword, bool includeDisabled, CancellationToken ct = default)
    {
        var select = Db.Select<Brand>();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            select = select.Where(a => a.BrandName.Contains(kw));
        }

        if (!includeDisabled) select = select.Where(a => a.Status == 1);

        var total = await select.CountAsync(ct);
        var items = await select.OrderBy(a => a.SortOrder).OrderByDescending(a => a.Id)
            .Page(page, pageSize).ToListAsync(ct);

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByNameAsync(string brandName, long excludeId = 0, CancellationToken ct = default)
        => await Db.Select<Brand>()
            .Where(a => a.BrandName == brandName && a.Id != excludeId)
            .AnyAsync(ct);

    /// <inheritdoc />
    public async Task<long> CountProductsAsync(long brandId, CancellationToken ct = default)
        => await Db.Select<Product>().Where(a => a.BrandId == brandId).CountAsync(ct);
}