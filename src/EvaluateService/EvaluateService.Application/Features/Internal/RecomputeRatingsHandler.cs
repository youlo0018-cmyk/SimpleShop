using Collaboration.Domain.Common;
using EvaluateService.Application.Services;
using EvaluateService.Domain.IRepository;
using MediatR;
using Microsoft.Extensions.Logging;

namespace EvaluateService.Application.Features.Internal;

/// <summary>全量重算商品与店铺评分（ScheduledService 每日 03:00 调用）。</summary>
/// <param name="WriteBack">是否把结果回写到商品表。</param>
public record RecomputeRatingsCommand(bool WriteBack = true) : IRequest<ApiResponse<RecomputeRatingsResult>>;

/// <summary>重算结果。</summary>
/// <param name="SpuCount">参与计算的商品数（只含有评价的）。</param>
/// <param name="MerchantCount">参与计算的店铺数。</param>
/// <param name="WrittenBack">成功回写的商品数。</param>
/// <param name="WriteBackSucceeded">回写是否成功。</param>
/// <param name="MerchantRatings">店铺评分（供 MerchantPlatformService 后续消费）。</param>
public sealed record RecomputeRatingsResult(
    int SpuCount, int MerchantCount, int WrittenBack, bool WriteBackSucceeded,
    IReadOnlyDictionary<long, decimal> MerchantRatings);

/// <summary>全量重算处理器。</summary>
/// <remarks>
/// <b>全量重算而不是增量更新</b>：增量要处理「新增评价加一分」「隐藏评价减一分」
/// 「删除评价减一分」三条路径，任何一条漏了都会让分数永久漂移，且漂移不可逆
/// （没人知道它偏了多少）。全量重算幂等、可重跑，跑几次结果都一样。
/// 代价是每天一次全表扫描——对评价这种量级完全可接受。
/// </remarks>
public sealed class RecomputeRatingsHandler
    : IRequestHandler<RecomputeRatingsCommand, ApiResponse<RecomputeRatingsResult>>
{
    private readonly IEvaluateRepository _repo;
    private readonly IProductPort _products;
    private readonly ILogger<RecomputeRatingsHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="repo">评价仓储。</param>
    /// <param name="products">商品服务端口（回写用）。</param>
    /// <param name="logger">日志器。</param>
    public RecomputeRatingsHandler(
        IEvaluateRepository repo, IProductPort products, ILogger<RecomputeRatingsHandler> logger)
    {
        _repo = repo;
        _products = products;
        _logger = logger;
    }

    /// <summary>执行重算。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>重算结果。</returns>
    public async Task<ApiResponse<RecomputeRatingsResult>> Handle(
        RecomputeRatingsCommand request, CancellationToken ct)
    {
        // 参与算分的 SPU 集合必须包含「评价已被全部隐藏」的那些。
        // 它们要拿到 0 分 0 条，从而把商品表里的旧评分清掉；
        // 否则「评价列表空了、商品还挂着 4.8 星」会永远持续下去。
        var spuIds = await _repo.GetAllEvaluatedSpuIdsAsync(ct).ConfigureAwait(false);

        if (spuIds.Count == 0)
        {
            _logger.LogInformation("没有任何有评价的商品，跳过重算");
            return ApiResults.Ok(new RecomputeRatingsResult(0, 0, 0, true,
                new Dictionary<long, decimal>()));
        }

        var ratings = await _repo.AggregateBySpuAsync(spuIds, ct).ConfigureAwait(false);
        var merchantRatings = await _repo.AggregateMerchantRatingsAsync(ct).ConfigureAwait(false);

        var written = 0;
        var writeBackOk = true;

        if (request.WriteBack)
        {
            var payload = ratings.Values
                .Select(a => new ProductRating(a.SpuId, a.AverageScore, a.Count))
                .ToList();

            writeBackOk = await _products.SyncRatingsAsync(payload, ct).ConfigureAwait(false);
            written = writeBackOk ? payload.Count : 0;

            if (!writeBackOk)
            {
                // 回写失败**不算整个任务失败**：评分已经算对了，只是没同步到商品表。
                // 下一次定时任务会重算并重试，商品表最多多滞后一天。
                // 抛错反而会让调度器认为任务失败而报警，掩盖真正的失败原因。
                _logger.LogError("评分已算出但回写商品表失败，将在下次定时任务重试");
            }
        }

        _logger.LogInformation(
            "评分重算完成：商品 {SpuCount} 个，店铺 {MerchantCount} 个，回写 {Written} 个",
            ratings.Count, merchantRatings.Count, written);

        return ApiResults.Ok(new RecomputeRatingsResult(
            ratings.Count, merchantRatings.Count, written, writeBackOk, merchantRatings));
    }
}
