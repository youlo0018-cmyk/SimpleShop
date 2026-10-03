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
}