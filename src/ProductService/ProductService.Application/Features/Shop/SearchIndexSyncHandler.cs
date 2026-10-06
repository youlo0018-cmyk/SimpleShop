using Collaboration.Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;
using ProductService.Application.Services;
using ProductService.Domain.Entities;
using ProductService.Domain.IRepository;
using ProductEntity = ProductService.Domain.Entities.Product;

namespace ProductService.Application.Features.Shop;

/// <summary>商品搜索索引对账处理器。</summary>
public sealed class SearchIndexSyncHandler
    : IRequestHandler<SyncSearchIndexCommand, ApiResponse<SearchIndexSyncResult>>
{
    private readonly IProductRepository _products;
    private readonly IProductSearchIndex _search;
    private readonly ILogger<SearchIndexSyncHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="products">商品仓储。</param>
    /// <param name="search">搜索索引。</param>
    /// <param name="logger">日志器。</param>
    public SearchIndexSyncHandler(
        IProductRepository products, IProductSearchIndex search, ILogger<SearchIndexSyncHandler> logger)
    {
        _products = products;
        _search = search;
        _logger = logger;
    }

    /// <summary>执行对账。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>对账统计。</returns>
    /// <remarks>
    /// 🔴 <b>这里刻意不做「删索引重建」</b>：重建期间索引是空的，
    /// 那段时间用户搜索会得到零结果，而且越热门越容易触发。差集对账全程索引可搜。
    /// </remarks>
    public async Task<ApiResponse<SearchIndexSyncResult>> Handle(
        SyncSearchIndexCommand request, CancellationToken ct)
    {
        var pageSize = Math.Clamp(request.PageSize, 1, 500);

        var indexed = (await _search.GetIndexedIdsAsync(ct).ConfigureAwait(false)).ToHashSet();

        var indexedBefore = indexed.Count;
        var dbTotal = 0;
        var missing = 0;
        var failed = 0;
        var seen = new HashSet<long>();

        for (var page = 1; ; page++)
        {
            // Status / AuditStatus 都传 0 = 不过滤：**下架和未审核的商品也要进索引**。
            // 因为 ES 侧才有「审核通过 + 已上架」的过滤，索引里存着才能被过滤掉。
            // 如果这里只同步上架商品，运营把商品下架后索引里那份就成了永远搜不到的幽灵。
            var (rows, _) = await _products.QueryPagedAsync(
                page, pageSize,
                new ProductQuery { Status = 0, AuditStatus = 0, Order = 0 },
                ct).ConfigureAwait(false);

            if (rows.Count == 0) break;

            dbTotal += rows.Count;

            foreach (var product in rows)
            {
                seen.Add(product.Id);

                if (indexed.Contains(product.Id)) continue;

                // 索引里没有 → 补写。写失败不中断整轮：它下一轮还会被扫到，
                // 中断反而会让后面所有商品都不被处理。
                if (await _search.IndexAsync(product, ct).ConfigureAwait(false))
                {
                    missing++;
                    indexed.Add(product.Id);
                }
                else
                {
                    failed++;
                }
            }

            if (rows.Count < pageSize) break;
        }

        // 孤儿：索引里有、库里没有（已软删或物理删）。留着的话用户能搜到一个点进去 404 的商品。
        var orphans = 0;
        if (request.DeleteOrphans)
        {
            // 🔴 候选孤儿**必须回库逐个确认**，不能只信上面那一轮分页扫描的 seen。
            //
            // 分页扫描期间数据在变（有人在新建 / 删除商品），页边界会整体漂移，
            // 于是某个真实存在的商品可能一次都没被扫到。只按 seen 判断的话，
            // 它会被当成孤儿从索引里删掉 —— 商品还在售，却搜不到，
            // 而且下一轮对账才会补回来（15 分钟）。
            //
            // 反过来误删的代价远大于多留一个孤儿文档：孤儿最多是「搜到点进去 404」，
            // 误删是「在售商品搜不到」，直接影响成交。
            var candidates = indexed.Where(a => !seen.Contains(a)).ToList();

            for (var i = 0; i < candidates.Count; i += 200)
            {
                var batch = candidates.Skip(i).Take(200).ToList();
                var alive = (await _products.GetByIdsAsync(batch, ct).ConfigureAwait(false))
                    .Select(a => a.Id)
                    .ToHashSet();

                foreach (var id in batch)
                {
                    // 回库查得到 → 不是孤儿，只是分页没扫到。保留索引文档。
                    if (alive.Contains(id)) continue;

                    if (await _search.DeleteAsync(id, ct).ConfigureAwait(false)) orphans++;
                }
            }
        }

        var result = new SearchIndexSyncResult(
            dbTotal, indexedBefore, dbTotal, missing, orphans, failed);

        _logger.LogInformation(
            "商品索引对账完成：库 {DbTotal} 个，索引 {Before} → {After}，补写 {Missing}，清理孤儿 {Orphans}，失败 {Failed}",
            result.DbProducts, result.IndexedBefore, result.IndexedAfter,
            result.Missing, result.OrphansRemoved, result.Failed);

        return ApiResults.Ok(result, $"对账完成，补写 {missing} 个，清理孤儿 {orphans} 个");
    }
}
