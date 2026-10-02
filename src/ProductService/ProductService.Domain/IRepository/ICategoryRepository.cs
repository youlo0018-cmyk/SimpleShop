namespace ProductService.Domain.IRepository;

/// <summary>分类仓储。</summary>
public interface ICategoryRepository
{
    /// <summary>列出全部未删除分类（按层级 + 排序）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分类列表。幂等只读。</returns>
    Task<List<Entities.Category>> ListAllAsync(CancellationToken ct = default);

    /// <summary>按 Id 取分类。</summary>
    /// <param name="id">分类 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分类或 null。幂等只读。</returns>
    Task<Entities.Category?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>判断某父分类下是否已有同名分类。</summary>
    /// <param name="parentId">父分类 Id，0 表示一级。</param>
    /// <param name="name">分类名。</param>
    /// <param name="excludeId">排除的 Id（编辑时传自身），可为 0。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>存在返回 true。幂等只读。</returns>
    Task<bool> ExistsByNameAsync(long parentId, string name, long excludeId = 0, CancellationToken ct = default);

    /// <summary>取某父分类的直接子分类数量。</summary>
    /// <param name="parentId">父分类 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>子分类数量。幂等只读。</returns>
    Task<long> CountChildrenAsync(long parentId, CancellationToken ct = default);

    /// <summary>插入分类。</summary>
    /// <param name="category">分类实体，Id 与审计字段由仓储基类填充。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新分类 Id。非幂等。</returns>
    Task<long> InsertAsync(Entities.Category category, CancellationToken ct = default);

    /// <summary>按字段更新分类。</summary>
    /// <param name="category">携带 Id 与待更新字段的实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> UpdateAsync(Entities.Category category, CancellationToken ct = default);

    /// <summary>软删分类。</summary>
    /// <param name="id">分类 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等：重复软删返回 0。</returns>
    Task<int> DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>统计挂在该分类下的商品数。</summary>
    /// <param name="categoryId">分类 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品数量。幂等只读。用于「有商品禁止删除」。</returns>
    Task<long> CountProductsAsync(long categoryId, CancellationToken ct = default);
}