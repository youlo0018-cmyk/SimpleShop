using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using EvaluateService.Domain.Entities;
using EvaluateService.Domain.Exceptions;
using EvaluateService.Domain.IRepository;
using EvaluateService.Domain.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace EvaluateService.Application.Features.Evaluate;

/// <summary>后台评价列表处理器。</summary>
public sealed class QueryAdminEvaluatesHandler
    : IRequestHandler<QueryAdminEvaluatesCommand, ApiResponse<EvaluatePageResult>>
{
    private readonly IEvaluateRepository _repo;

    /// <summary>构造处理器。</summary>
    /// <param name="repo">评价仓储。</param>
    public QueryAdminEvaluatesHandler(IEvaluateRepository repo) => _repo = repo;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>评价分页，含隐藏原因与真实昵称。</returns>
    public async Task<ApiResponse<EvaluatePageResult>> Handle(
        QueryAdminEvaluatesCommand request, CancellationToken ct)
    {
        var filter = new EvaluateAdminFilter(
            request.SpuId, request.MerchantId, request.StarScore,
            request.OnlyHidden, request.Page, request.PageSize);

        var page = await _repo.PageForAdminAsync(filter, ct).ConfigureAwait(false);
        var dtos = await EvaluateAssembler.BuildListAsync(_repo, page.Items, forAdmin: true, ct);

        decimal average = 0m;
        var count = 0;
        if (request.SpuId > 0)
        {
            var rating = await _repo.AggregateBySpuAsync([request.SpuId], ct).ConfigureAwait(false);
            if (rating.TryGetValue(request.SpuId, out var r))
            {
                average = r.AverageScore;
                count = r.Count;
            }
        }

        return ApiResults.Ok(new EvaluatePageResult(
            dtos, page.Total, page.Page, page.PageSize,
            average, EvaluateCalculator.DisplayScore(average), count));
    }
}

/// <summary>隐藏 / 取消隐藏评价处理器。</summary>
public sealed class HideEvaluateHandler : IRequestHandler<HideEvaluateCommand, ApiResponse>
{
    private readonly IEvaluateRepository _repo;

    /// <summary>构造处理器。</summary>
    /// <param name="repo">评价仓储。</param>
    public HideEvaluateHandler(IEvaluateRepository repo) => _repo = repo;

    /// <summary>执行隐藏。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// 隐藏的是**展示**不是数据：<c>is_deleted</c> 保持 false，只是打 <c>is_hidden</c>。
    /// 软删会让评价在客户侧「凭空消失」，用户会以为评价丢了或没提交成功；
    /// 隐藏则客户能在「我的评价」里看到「已被隐藏」的状态，配合后台的隐藏原因可追溯。
    /// </remarks>
    public async Task<ApiResponse> Handle(HideEvaluateCommand request, CancellationToken ct)
    {
        // 🔴 操作人从**令牌租户上下文**取，不从请求体取。
        // 之前这里是 request.OperatorId，调用方可以自称任意操作人，
        // 而 hidden_by_id 是「谁隐藏了这条评价」的审计凭据 ——
        // 审计人可伪造，这条审计记录就没有意义了。
        var ctx = TenantContextHolder.Current;
        if (ctx.UserId <= 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.Unauthorized, "登录状态已失效，请重新登录");
        }

        var evaluate = await _repo.GetByIdAsync(request.EvaluateId, ct).ConfigureAwait(false);
        if (evaluate is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "评价不存在");
        }

        var ok = await _repo.SetHiddenAsync(
            request.EvaluateId, request.IsHidden,
            (request.HiddenReason ?? string.Empty).Trim(),
            ctx.UserId, ct).ConfigureAwait(false);

        if (!ok) return ApiResponseFactory.Fail(BaseApiResponseCode.InternalError, "操作失败，请重试");

        return ApiResponseFactory.Ok(request.IsHidden ? "已隐藏" : "已恢复显示");
    }
}

/// <summary>回复评价处理器。</summary>
public sealed class ReplyEvaluateHandler : IRequestHandler<ReplyEvaluateCommand, ApiResponse<long>>
{
    private readonly IEvaluateRepository _repo;
    private readonly ILogger<ReplyEvaluateHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="repo">评价仓储。</param>
    /// <param name="logger">日志器。</param>
    public ReplyEvaluateHandler(IEvaluateRepository repo, ILogger<ReplyEvaluateHandler> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    /// <summary>执行回复。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回复 Id。</returns>
    public async Task<ApiResponse<long>> Handle(ReplyEvaluateCommand request, CancellationToken ct)
    {
        // 🔴 回复人从**令牌租户上下文**取，不从请求体取。
        // 之前是 request.OperatorId / request.OperatorName —— 调用方可以自称
        // 「平台官方」或「商家」，而回复人是评价区的归属凭据：
        // 伪造它就能把商户回复伪装成平台回复（或反过来）。
        var ctx = TenantContextHolder.Current;

        if (ctx.UserId <= 0)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.Unauthorized, "登录状态已失效，请重新登录");
        }

        var evaluate = await _repo.GetByIdAsync(request.EvaluateId, ct).ConfigureAwait(false);
        if (evaluate is null)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.NotFound, "评价不存在");
        }

        // 隐藏后不再接受新回复：违规内容不该再被回复「洗白」
        if (evaluate.IsHidden)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BusinessError, "该评价已被隐藏，无法回复");
        }

        var reply = new EvaluateReply
        {
            EvaluateId = request.EvaluateId,
            AppendId = request.AppendId,
            Content = request.ReplyContent.Trim(),
            ReplyType = request.ReplyType,
            ReplyById = ctx.UserId,
            ReplyByName = ctx.UserName
        };

        try
        {
            var id = await _repo.InsertReplyAsync(reply, ct).ConfigureAwait(false);
            return ApiResults.Ok(id, "回复成功");
        }
        catch (DuplicateReplyException ex)
        {
            _logger.LogInformation(ex, "评价 {EvaluateId} 已被 {ReplyType} 回复过，重复回复被拦下",
                request.EvaluateId, request.ReplyType);

            return ApiResults.Fail<long>(BaseApiResponseCode.BusinessError, ex.Message);
        }
    }
}
