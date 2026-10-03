using ProductService.Domain.Entities;

namespace ProductService.Domain.IRepository;

/// <summary>商品（SPU）仓储。</summary>
public interface IProductRepository
{
    /// <summary>分页查询商品。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数，1-100。</param>
    /// <param name="query">筛选条件（关键字 / 分类 / 品牌 / 上下架 / 审核状态）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品列表与总数。幂等只读。</returns>
    /// <remarks>
    /// 软删与租户过滤由 GlobalFilter 注入，这里不要手写。
    /// 排序固定为「先按审核状态、再按排序值、最后按 Id」——
    /// 最后那个 Id 兜底是为了同值行顺序稳定，否则翻页会出现重复或漏掉。
    /// </remarks>
    Task<(List<Product> Items, long Total)> QueryPagedAsync(
        int page, int pageSize, ProductQuery query, CancellationToken ct = default);
    /// <summary>按 Id 集合批量取商品（搜索召回后回库取权威数据用）。</summary>
    /// <param name="ids">商品 Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的商品；<b>不保证与传入顺序一致</b>，调用方需自行按 Id 重排。</returns>
    /// <remarks>
    /// 搜索只用 ES 召回 Id，价格与状态一律回这里取权威值——否则会出现
    /// 「搜索结果显示有货，点进去发现已下架」「列表显示 99、结算 129」。
    /// 一次 IN 查询拿回整页，避免 N+1。
    /// </remarks>
    Task<List<Product>> GetByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// <summary>按 Id 取商品。</summary>
    /// <param name="id">商品 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品或 null。幂等只读。</returns>
    Task<Product?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>取商品的全部 SKU（含停用）。</summary>
    /// <param name="productId">商品 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>SKU 列表。幂等只读。</returns>
    Task<List<Sku>> GetSkusAsync(long productId, CancellationToken ct = default);

    /// <summary>取商品的规格项（按 SortOrder 排序）。</summary>
    /// <param name="productId">商品 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>规格项列表。幂等只读。</returns>
    Task<List<ProductSpec>> GetSpecsAsync(long productId, CancellationToken ct = default);

    /// <summary>取商品的规格值（按 SortOrder 排序）。</summary>
    /// <param name="productId">商品 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>规格值列表。幂等只读。</returns>
    Task<List<ProductSpecValue>> GetSpecValuesAsync(long productId, CancellationToken ct = default);

    /// <summary>取 SKU 与规格值的关联。</summary>
    /// <param name="productId">商品 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>关联列表。幂等只读。</returns>
    Task<List<SkuSpecValue>> GetSkuSpecLinksAsync(long productId, CancellationToken ct = default);

    /// <summary>按商品 Id 集合批量取**启用中**的 SKU 价格行。</summary>
    /// <param name="productIds">商品 Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>SKU Id / 商品 Id / 售价。</returns>
    /// <remarks>
    /// 列表页要给每个商品算到手价，而到手价是按 **SKU** 算的。
    /// 逐个商品调 <see cref="GetSkusAsync"/> 就是 N+1——一屏 20 个商品就是 20 条额外查询。
    /// 一次批量取回，本地按商品分组，再合成一次营销试算，总共 2 条查询。
    /// </remarks>
    Task<List<SkuPriceRow>> GetSkuPriceRowsAsync(
        IReadOnlyCollection<long> productIds, CancellationToken ct = default);


    /// <summary>按 SKU 编码取 SKU。</summary>
    /// <param name="skuCode">SKU 编码（Upsert 的依据）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>SKU 或 null。幂等只读。</returns>
    Task<Sku?> GetSkuByCodeAsync(string skuCode, CancellationToken ct = default);

    /// <summary>判断 SKU 编码是否被别的商品占用。</summary>
    /// <param name="skuCode">SKU 编码。</param>
    /// <param name="excludeId">排除的 SKU Id（编辑时传自身），可为 0。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>存在返回 true。幂等只读。</returns>
    Task<bool> ExistsSkuCodeAsync(string skuCode, long excludeId = 0, CancellationToken ct = default);

    /// <summary>插入商品。<b>实现由 CrudRepository&lt;Product&gt; 继承而来</b>，雪花 Id 与审计时间戳在基类里填。</summary>
    /// <param name="product">商品实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新商品 Id。非幂等。</returns>
    Task<long> InsertAsync(Product product, CancellationToken ct = default);

    /// <summary>按字段更新商品。<b>实现由 CrudRepository&lt;Product&gt; 继承而来</b>。</summary>
    /// <param name="product">携带 Id 与待更新字段的实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> UpdateAsync(Product product, CancellationToken ct = default);

    /// <summary>批量插入规格项与规格值。</summary>
    /// <param name="specs">规格项。</param>
    /// <param name="values">规格值。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。非幂等。</returns>
    Task<int> InsertSpecsAsync(IReadOnlyCollection<ProductSpec> specs, IReadOnlyCollection<ProductSpecValue> values, CancellationToken ct = default);

    /// <summary>软删某商品的全部规格项、规格值与 SKU 关联（不含 SKU 本身）。</summary>
    /// <param name="productId">商品 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> SoftDeleteSpecsAsync(long productId, CancellationToken ct = default);

    /// <summary>批量 Upsert SKU（按 SkuCode），并重建 SKU 与规格值的关联。</summary>
    /// <param name="skus">SKU 列表。<b>出参</b>：调用后每个实体的 Id 会被填成真实的雪花 Id。</param>
    /// <param name="linksBySkuCode">SKU 编码 → 该 SKU 要挂的规格值。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>Upsert 的 SKU 行数。</returns>
    /// <remarks>
    /// 「按编码 Upsert」是这个接口的核心语义：前端编辑页提交的是完整 SKU 列表，
    /// 带 SkuCode 的已存在行更新价格/状态，不带 SkuCode 的新插入，
    /// 本次提交里消失的旧编码软删。这样前端不必维护 SKU 的增删标记。
    ///
    /// 链接用「编码 → 规格值」而不是「SkuId → 规格值」，原因见 <see cref="SkuSpecLink"/>。
    /// </remarks>
    Task<int> UpsertSkusAsync(
        IReadOnlyCollection<Sku> skus,
        IReadOnlyDictionary<string, IReadOnlyList<SkuSpecLink>> linksBySkuCode,
        CancellationToken ct = default);

    /// <summary>软删不在给定编码集合里的 SKU。</summary>
    /// <param name="productId">商品 Id。</param>
    /// <param name="keepCodes">要保留的 SKU 编码集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>软删的行数。</returns>
    Task<int> SoftDeleteSkusNotInAsync(long productId, IReadOnlyCollection<string> keepCodes, CancellationToken ct = default);

    /// <summary>重新计算商品的最低 / 最高售价。</summary>
    /// <param name="productId">商品 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> RefreshPriceRangeAsync(long productId, CancellationToken ct = default);

    /// <summary>统计引用该分类的商品数（分类删除前置校验）。</summary>
    /// <param name="categoryId">分类 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品数量。幂等只读。</returns>
    Task<long> CountProductsAsync(long categoryId, CancellationToken ct = default);

    /// <summary>软删商品。</summary>
    /// <param name="id">商品 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> DeleteProductAsync(long id, CancellationToken ct = default);
}

/// <summary>SKU 的价格行，只带列表页算到手价必需的字段。</summary>
/// <param name="SkuId">SKU Id。</param>
/// <param name="ProductId">所属商品 Id。</param>
/// <param name="Price">售价。</param>
public readonly record struct SkuPriceRow(long SkuId, long ProductId, decimal Price);