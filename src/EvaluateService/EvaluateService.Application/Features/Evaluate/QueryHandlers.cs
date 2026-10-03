using Collaboration.Domain.Common;
using EvaluateService.Domain.IRepository;
using EvaluateService.Domain.Services;
using MediatR;

namespace EvaluateService.Application.Features.Evaluate;

/// <summary>商品评价列表处理器（商品详情页）。</summary>
/// <remarks>
/// 均分是**当前页之外全量算出来的**，不是只算这一页：
/// 只按当前页 10 条算均分，用户翻到第 3 页会看到另一个分数。
/// </remarks>
public sealed class QuerySpuEvaluatesHandler
    : IRequestHandler<QuerySpuEvaluatesCommand, ApiResponse<EvaluatePageResult>>
{
    private readonly IEvaluateRepository _repo;

    /// <summary>构造处理器。</summary>
    /// <param name="repo">评价仓储。</param>
    public QuerySpuEvaluatesHandler(IEvaluateRepository repo) => _repo = repo;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>评价分页。</returns>
    public async Task<ApiResponse<EvaluatePageResult>> Handle(
        QuerySpuEvaluatesCommand request, CancellationToken ct)
    {
        var page = await _repo.PageBySpuAsync(request.SpuId, request.SkuId, request.Page, request.PageSize, ct);

        var rating = await _repo.AggregateBySpuAsync([request.SpuId], ct);
        var average = rating.TryGetValue(request.SpuId, out var r) ? r.AverageScore : 0m;

        var dtos = await EvaluateAssembler.BuildListAsync(_repo, page.Items, forAdmin: false, ct);

        return ApiResults.Ok(new EvaluatePageResult(
            dtos, page.Total, page.Page, page.PageSize,
            average, EvaluateCalculator.DisplayScore(average),
            rating.TryGetValue(request.SpuId, out var rr) ? rr.Count : 0));
    }
}

/// <summary>我的评价列表处理器。</summary>
public sealed class QueryMyEvaluatesHandler
    : IRequestHandler<QueryMyEvaluatesCommand, ApiResponse<EvaluatePageResult>>
{
    private readonly IEvaluateRepository _repo;

    /// <summary>构造处理器。</summary>
    /// <param name="repo">评价仓储。</param>
    public QueryMyEvaluatesHandler(IEvaluateRepository repo) => _repo = repo;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>评价分页。</returns>
    public async Task<ApiResponse<EvaluatePageResult>> Handle(
        QueryMyEvaluatesCommand request, CancellationToken ct)
    {
        var page = await _repo.PageByCustomerAsync(request.CustomerId, request.Page, request.PageSize, ct);
        var dtos = await EvaluateAssembler.BuildListAsync(_repo, page.Items, forAdmin: false, ct);

        // 「我的评价」跨多个 SPU，均分无意义，这里全给 0 / 展示 5.0
        return ApiResults.Ok(new EvaluatePageResult(
            dtos, page.Total, page.Page, page.PageSize, 0m, EvaluateCalculator.DefaultScore, 0));
    }
}
