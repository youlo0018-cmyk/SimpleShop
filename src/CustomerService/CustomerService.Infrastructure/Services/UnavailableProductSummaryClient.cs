using CustomerService.Application.Services;

namespace CustomerService.Infrastructure.Services;

/// <summary>未配置商品服务地址时的空实现。</summary>
/// <remarks>
/// 收藏页可以在没有商品摘要的情况下回退为「商品 Id + 收藏时间」，
/// 因此这里不抛异常；配置补上后由 <c>HttpProductSummaryClient</c> 接管。
/// </remarks>
public sealed class UnavailableProductSummaryClient : IProductSummaryClient
{
    /// <inheritdoc />
    public Task<IReadOnlyDictionary<long, ProductSummary>> GetSpuSummariesAsync(
        IReadOnlyCollection<long> spuIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<long, ProductSummary>>(
            new Dictionary<long, ProductSummary>());
}
