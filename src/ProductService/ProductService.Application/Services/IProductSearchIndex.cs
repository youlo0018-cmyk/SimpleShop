

using ProductService.Domain.Entities;

namespace ProductService.Application.Services;

/// <summary>搜索配置。</summary>
public sealed class ProductSearchOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Elasticsearch";

    /// <summary>ES 地址。</summary>
    public string Url { get; set; } = "http://127.0.0.1:9200";

    /// <summary>商品索引名。</summary>
    public string IndexName { get; set; } = "simpleshop_product";
}

/// <summary>
/// 商品搜索索引。
/// </summary>
/// <remarks>
/// <para><b>一条铁律：ES 只负责召回，权威数据一律回 PostgreSQL 取。</b>
/// 商品的价格、上下架、审核状态都是会变的字段，一旦直接读 ES 里的副本，
/// 就会出现「搜索结果显示有货，点进去发现已下架」「列表显示 99，点进去 129」——
/// 用户对价格的不信任就是这么来的，而且极难排查（两个库对不上，但都不知道哪个是对的）。
/// 所以索引里只存够排序与过滤的字段，<b>不含价格与状态</b>，只回 Id。</para>
///
/// <para>索引写失败<b>不阻塞业务</b>：保存商品是主链路，索引是加速手段。
/// ES 挂了应该只是「搜不到」，不该变成「商品保存不了」。索引靠补偿任务补齐。</para>
/// </remarks>
public interface IProductSearchIndex
{
    /// <summary>确保索引存在（幂等，可重复调用）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否可用。</returns>
    Task<bool> EnsureIndexAsync(CancellationToken ct = default);

    /// <summary>写入 / 覆盖一个商品。</summary>
    /// <param name="product">商品。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否成功。</returns>
    Task<bool> IndexAsync(Product product, CancellationToken ct = default);

    /// <summary>按 Id 覆盖商品状态字段（审核 / 上下架变化）。</summary>
    /// <param name="productId">商品 Id。</param>
    /// <param name="auditStatus">审核状态。</param>
    /// <param name="status">上下架状态。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否成功。</returns>
    Task<bool> UpdateStatusAsync(long productId, int auditStatus, int status, CancellationToken ct = default);

    /// <summary>删除一个商品。</summary>
    /// <param name="productId">商品 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否成功。</returns>
    Task<bool> DeleteAsync(long productId, CancellationToken ct = default);

    /// <summary>按关键词搜索商品 <b>Id</b>。</summary>
    /// <param name="keyword">关键词，空表示不加关键词条件（只按类目过滤）。</param>
    /// <param name="categoryId">分类过滤，0 表示不限。</param>
    /// <param name="brandId">品牌过滤，0 表示不限。</param>
    /// <param name="from">跳过前多少条。</param>
    /// <param name="size">取多少条。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的商品 Id 列表（已按相关度 / 销量排序）。</returns>
    Task<IReadOnlyList<long>> SearchIdsAsync(
        string keyword, long categoryId, long brandId, int from, int size, CancellationToken ct = default);

    /// <summary>取索引里当前存在的全部商品 Id。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品 Id 集合；索引不存在时返回空集合。</returns>
    /// <remarks>
    /// 补偿任务用它和数据库做<b>差集对账</b>：库里有而索引没有 → 补写，
    /// 索引有而库里没有（已删）→ 从索引删掉。
    ///
    /// <para>上限 1 万条（用 <c>_search</c> 的 size 上限实现）。超过这个量级
    /// 应改用 scroll 或 PIT——否则一次要拉回全部 Id，内存与响应体都会失控。</para>
    /// </remarks>
    Task<IReadOnlyCollection<long>> GetIndexedIdsAsync(CancellationToken ct = default);

    /// <summary>删除并重建索引（切分词器时用）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否成功。</returns>
    Task<bool> RecreateIndexAsync(CancellationToken ct = default);
}