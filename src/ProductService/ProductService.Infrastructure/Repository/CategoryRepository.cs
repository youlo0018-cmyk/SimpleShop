using Collaboration.Domain.Repository;
using FreeSql;
using ProductService.Domain.Entities;
using ProductService.Domain.IRepository;

namespace ProductService.Infrastructure.Repository;

/// <summary>
/// 分类仓储实现。InsertAsync / UpdateAsync / DeleteAsync 继承 CrudRepository——
/// 基类负责填雪花 Id 与审计时间戳，这里不要重复实现（否则会绕过 Id 填充）。
/// </summary>
public sealed class CategoryRepository : CrudRepository<Category>, ICategoryRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public CategoryRepository(IFreeSql freeSql) : base(freeSql)
    {
    }

    /// <inheritdoc />
    public async Task<List<Category>> ListAllAsync(CancellationToken ct = default)
        => await Db.Select<Category>()
            .OrderBy(a => a.Level)
            .OrderBy(a => a.ParentId)
            .OrderBy(a => a.SortOrder)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<bool> ExistsByNameAsync(long parentId, string name, long excludeId = 0, CancellationToken ct = default)
        => await Db.Select<Category>()
            .Where(a => a.ParentId == parentId && a.CategoryName == name && a.Id != excludeId)
            .AnyAsync(ct);
    /// <inheritdoc />
    public async Task<long> CountChildrenAsync(long parentId, CancellationToken ct = default)
        => await Db.Select<Category>().Where(a => a.ParentId == parentId).CountAsync(ct);

    /// <inheritdoc />
    public async Task<long> CountProductsAsync(long categoryId, CancellationToken ct = default)
        => await Db.Select<Product>().Where(a => a.CategoryId == categoryId).CountAsync(ct);
}