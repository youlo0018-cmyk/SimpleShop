namespace ProductService.Domain.IRepository;

/// <summary>商品查询条件。放在 Domain 而不是复用应用层的 QueryProductsCommand。</summary>
/// <remarks>
/// 仓储契约属于 Domain 层，不能反过来依赖 Application 层的命令类型——那是把层级依赖倒过来了。
/// 应用层负责把 Command 翻译成这个对象，两者不要混用。
/// </remarks>
public sealed record ProductQuery
{
    /// <summary>按商品名模糊搜索。空表示不过滤。</summary>
    public string Keyword { get; init; } = string.Empty;

    /// <summary>按分类过滤。0 表示不过滤。</summary>
    public long CategoryId { get; init; }

    /// <summary>按品牌过滤。0 表示不过滤。</summary>
    public long BrandId { get; init; }

    /// <summary>按上下架状态过滤。0 表示不过滤。</summary>
    public int Status { get; init; }

    /// <summary>按审核状态过滤。0 表示不过滤。</summary>
    public int AuditStatus { get; init; }

    /// <summary>排序方式，见 <see cref="ProductSorts"/>。0 表示默认（排序值 + 销量）。</summary>
    /// <remarks>
    /// 排序交给 SQL 做，不要查出来在内存里排：内存排序只能排**当前页**，
    /// 「按价格从低到高」的第一页就不是最便宜的那些，用户一眼就看出来是错的。
    /// 按到手价排序是唯一没法交给 SQL 的（要跨服务试算），那一条走应用层的特殊路径。
    /// </remarks>
    public int Order { get; init; }
}

/// <summary>商品排序方式（可由 SQL 直接完成的那些）。</summary>
public static class ProductSorts
{
    /// <summary>默认：运营排序值优先，其次销量高。</summary>
    public const int Default = 0;

    /// <summary>销量从高到低。</summary>
    public const int SalesDesc = 1;

    /// <summary>最新创建在前。</summary>
    public const int Newest = 2;
}