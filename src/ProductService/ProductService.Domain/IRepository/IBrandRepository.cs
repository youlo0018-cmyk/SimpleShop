namespace ProductService.Domain.IRepository;

/// <summary>品牌仓储。品牌是商品的<b>选填项</b>，所以这张表可以为空（DATA_SPEC 5.5）。</summary>
public interface IBrandRepository
{
    /// <summary>分页查询品牌。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数，1-100。</param>
    /// <param name="keyword">按品牌名模糊搜索。</param>
    /// <param name="includeDisabled">是否包含停用品牌。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>品牌列表与总数。幂等只读。</returns>
    Task<(List<Entities.Brand> Items, long Total)> QueryPagedAsync(
        int page, int pageSize, string keyword, bool includeDisabled, CancellationToken ct = default);

    /// <summary>按 Id 取品牌。</summary>
    /// <param name="id">品牌 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>品牌或 null。幂等只读。</returns>
    Task<Entities.Brand?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>判断品牌名是否已被占用。</summary>
    /// <param name="brandName">品牌名。</param>
    /// <param name="excludeId">排除的 Id（编辑时传自身），可为 0。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>存在返回 true。幂等只读。</returns>
    Task<bool> ExistsByNameAsync(string brandName, long excludeId = 0, CancellationToken ct = default);

    /// <summary>统计引用该品牌的商品数。</summary>
    /// <param name="brandId">品牌 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品数量。幂等只读。用于「有商品禁止删除」。</returns>
    Task<long> CountProductsAsync(long brandId, CancellationToken ct = default);

    /// <summary>插入品牌。</summary>
    /// <param name="brand">品牌实体，Id 与审计字段由仓储基类填充。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新品牌 Id。非幂等。</returns>
    Task<long> InsertAsync(Entities.Brand brand, CancellationToken ct = default);

    /// <summary>按字段更新品牌。</summary>
    /// <param name="brand">携带 Id 与待更新字段的实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> UpdateAsync(Entities.Brand brand, CancellationToken ct = default);

    /// <summary>软删品牌。</summary>
    /// <param name="id">品牌 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> DeleteAsync(long id, CancellationToken ct = default);
}