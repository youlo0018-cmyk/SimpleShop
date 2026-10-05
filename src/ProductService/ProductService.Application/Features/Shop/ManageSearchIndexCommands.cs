using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProductService.Application.Services;
using ProductService.Domain.IRepository;

namespace ProductService.Application.Features.Shop;

/// <summary>后台「重建商品索引」。</summary>
public record ReindexProductsCommand(bool DeleteOrphans = true, int PageSize = 200)
    : IRequest<ApiResponse<ReindexResult>>;

/// <summary>后台「索引对账」：只看差异，不写 ES。</summary>
/// <remarks>
/// 与 <see cref="ReindexProductsCommand"/> 分开是因为两者的风险完全不同：
/// 对账是纯只读，任何时候点都安全；而重建会写 ES。合成一个接口再加个 DryRun 开关的话，
/// 「忘了传 DryRun」就是一次真实的全量写入——所以宁可分成两个端点，
/// 让「只读」这件事在 URL 上就看得出来。
/// </remarks>
public record ReconcileSearchIndexCommand(int PageSize = 500)
    : IRequest<ApiResponse<ReconcileResult>>;

/// <summary>重建结果。</summary>
/// <param name="DbProducts">库里的商品总数。</param>
/// <param name="IndexedBefore">重建前索引里的商品数。</param>
/// <param name="IndexedAfter">重建后索引里的商品数。</param>
/// <param name="Missing">补写进索引的商品数。</param>
/// <param name="OrphansRemoved">从索引里清掉的孤儿文档数。</param>
/// <param name="Failed">写入失败的商品数，下一轮会再试。</param>
public sealed record ReindexResult(
    int DbProducts, int IndexedBefore, int IndexedAfter,
    int Missing, int OrphansRemoved, int Failed);

/// <summary>对账结果。</summary>
/// <param name="DbProducts">本次扫描到的商品数。</param>
/// <param name="IndexedCount">索引里的商品数。</param>
/// <param name="MissingInIndex">库里有、索引里没有的商品数（需要补写）。</param>
/// <param name="OrphanInIndex">索引里有、库里没有的商品数（已删商品，需要清理）。</param>
/// <param name="Consistent">两边是否已经一致。扫描不完整时恒为 false。</param>
/// <param name="MissingSampleIds">缺失商品的 Id 样本，最多 20 个，供界面直接跳转核对。</param>
/// <param name="OrphanSampleIds">孤儿商品的 Id 样本，最多 20 个。</param>
/// <param name="MaxScanned">本次最多扫描多少个商品。</param>
/// <param name="Truncated">扫描是否已达上限。结果不完整。</param>
public sealed record ReconcileResult(
    int DbProducts, int IndexedCount, int MissingInIndex, int OrphanInIndex, bool Consistent,
    IReadOnlyList<long> MissingSampleIds, IReadOnlyList<long> OrphanSampleIds,
    int MaxScanned, bool Truncated);

/// <summary>重建商品索引处理器。</summary>
public sealed class ReindexProductsHandler : IRequestHandler<ReindexProductsCommand, ApiResponse<ReindexResult>>
{
    private readonly IMediator _mediator;

    /// <summary>构造处理器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    /// <remarks>
    /// 复用 <see cref="SearchIndexSyncHandler"/>（走 <c>Send</c> 而不是 new 出来），
    /// 否则对账逻辑会有两份实现，两份慢慢漂移。
    /// 而且**必须走 MediatR**：直接 new 处理器就绕过了校验管道。
    /// </remarks>
    public ReindexProductsHandler(IMediator mediator) => _mediator = mediator;

    /// <inheritdoc />
    /// <remarks>
    /// 仍然是**差集对账**而不是「删索引重建」：重建期间索引是空的，
    /// 那段时间用户搜索会拿到零结果，而且越是热门商品越容易触发。
    /// 切分词器那种必须换索引的场景才用 <c>RecreateIndexAsync</c>，不在这里。
    /// </remarks>
    public async Task<ApiResponse<ReindexResult>> Handle(
        ReindexProductsCommand request, CancellationToken ct)
    {
        var response = await _mediator.Send(
            new SyncSearchIndexCommand(request.PageSize, request.DeleteOrphans), ct).ConfigureAwait(false);

        if (!response.Success || response.Data is null)
        {
            return ApiResults.Fail<ReindexResult>(
                BaseApiResponseCode.InternalError, response.Message ?? "重建索引失败");
        }

        var d = response.Data;
        return ApiResults.Ok(
            new ReindexResult(
                d.DbProducts, d.IndexedBefore, d.IndexedAfter,
                d.Missing, d.OrphansRemoved, d.Failed),
            $"重建完成，补写 {d.Missing} 个，清理孤儿 {d.OrphansRemoved} 个，失败 {d.Failed} 个");
    }
}

/// <summary>索引对账处理器（只读）。</summary>
public sealed class ReconcileSearchIndexHandler
    : IRequestHandler<ReconcileSearchIndexCommand, ApiResponse<ReconcileResult>>
{
    /// <summary>样本最多回多少个 Id。</summary>
    private const int SampleLimit = 20;

    private readonly IProductRepository _products;
    private readonly IProductSearchIndex _search;
    private readonly ILogger<ReconcileSearchIndexHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="products">商品仓储。</param>
    /// <param name="search">搜索索引。</param>
    /// <param name="logger">日志器。</param>
    public ReconcileSearchIndexHandler(
        IProductRepository products,
        IProductSearchIndex search,
        ILogger<ReconcileSearchIndexHandler> logger)
    {
        _products = products;
        _search = search;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>纯只读</b>：算完差集就返回，一个字节都不往 ES 写。
    /// 这样运营可以随时点开看一眼「索引现在健不健康」，而不必担心点了就引发全量写入。
    ///
    /// <para>扫库有上限（<see cref="ReconcileSearchIndexCommand.PageSize"/> × 页数）：
    /// 商品量上万时一次全表扫会让这个接口明显变慢，而它只是个诊断工具。
    /// 扫不完就如实返回 <c>Truncated = true</c>，**不假装扫完了**——
    /// 报一个假的「已一致」比报「只扫了前 5000 条」危险得多。</para>
    /// </remarks>
    public async Task<ApiResponse<ReconcileResult>> Handle(
        ReconcileSearchIndexCommand request, CancellationToken ct)
    {
        var pageSize = Math.Clamp(request.PageSize, 1, 500);
        var maxScanned = pageSize * 20;

        var indexed = (await _search.GetIndexedIdsAsync(ct).ConfigureAwait(false)).ToHashSet();
        var indexedCount = indexed.Count;

        var dbIds = new HashSet<long>();
        var missing = new List<long>();
        var scanned = 0;

        for (var page = 1; scanned < maxScanned; page++)
        {
            var (rows, _) = await _products.QueryPagedAsync(
                page, pageSize,
                new ProductQuery { Status = 0, AuditStatus = 0, Order = 0 },
                ct).ConfigureAwait(false);

            if (rows.Count == 0) break;

            foreach (var product in rows)
            {
                dbIds.Add(product.Id);
                if (!indexed.Contains(product.Id)) missing.Add(product.Id);
            }

            scanned += rows.Count;
            if (rows.Count < pageSize) break;
        }

        // 孤儿：索引里有、库里查不到。库里查不到可能是「已软删 / 已物理删」，
        // 也可能只是本轮没扫到（Truncated）——所以扫描不完整时不能把它当成真孤儿。
        var truncated = scanned >= maxScanned;
        var orphans = truncated ? new List<long>() : indexed.Where(id => !dbIds.Contains(id)).Order().ToList();

        var consistent = !truncated && missing.Count == 0 && orphans.Count == 0;

        _logger.LogInformation(
            "商品索引对账（只读）：扫库 {Scanned} 个，索引 {Indexed} 个，缺失 {Missing}，孤儿 {Orphans}{Truncated}",
            scanned, indexedCount, missing.Count, orphans.Count,
            truncated ? "（扫描已达上限，结果不完整）" : string.Empty);

        var result = new ReconcileResult(
            scanned, indexedCount, missing.Count, orphans.Count, consistent,
            missing.Take(SampleLimit).Order().ToList(),
            orphans.Take(SampleLimit).ToList(),
            maxScanned, truncated);

        return ApiResults.Ok(
            result,
            truncated
                ? $"扫描已达上限（{maxScanned} 个），结果仅供参考"
                : consistent ? "索引与数据库一致" : $"缺失 {missing.Count} 个，孤儿 {orphans.Count} 个");
    }
}

/// <summary>搜索索引后台命令的校验器注册。</summary>
public static class ManageSearchIndexValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddManageSearchIndexValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<ReindexProductsCommand>, ReindexProductsValidator>();
        services.AddScoped<IValidator<ReconcileSearchIndexCommand>, ReconcileSearchIndexValidator>();
    }

    /// <summary>重建索引参数校验。</summary>
    private sealed class ReindexProductsValidator : AbstractValidator<ReindexProductsCommand>
    {
        /// <summary>构造校验器。</summary>
        public ReindexProductsValidator()
            => RuleFor(x => x.PageSize).InclusiveBetween(1, 500).WithMessage("每批处理量在 1 ~ 500 之间");
    }

    /// <summary>对账参数校验。</summary>
    private sealed class ReconcileSearchIndexValidator : AbstractValidator<ReconcileSearchIndexCommand>
    {
        /// <summary>构造校验器。</summary>
        public ReconcileSearchIndexValidator()
            => RuleFor(x => x.PageSize).InclusiveBetween(1, 500).WithMessage("每批扫描量在 1 ~ 500 之间");
    }
}
