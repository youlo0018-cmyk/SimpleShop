using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using EvaluateService.Domain.Entities;
using EvaluateService.Domain.Exceptions;
using EvaluateService.Domain.IRepository;
using EvaluateService.Domain.Services;
using MediatR;

namespace EvaluateService.Application.Features.Evaluate;

/// <summary>追评处理器。</summary>
/// <remarks>
/// 三条限制都在服务端强制，<b>不靠前端禁用按钮</b>：
/// 最多 3 条、首评后 30 天内、只能追自己的评价。
/// 前端按钮禁用只是体验，真正拦住恶意调用的是这里。
/// </remarks>
public sealed class AppendEvaluateHandler : IRequestHandler<AppendEvaluateCommand, ApiResponse<long>>
{
    private readonly IEvaluateRepository _repo;

    /// <summary>构造处理器。</summary>
    /// <param name="repo">评价仓储。</param>
    public AppendEvaluateHandler(IEvaluateRepository repo) => _repo = repo;

    /// <summary>执行追评。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>追评 Id。</returns>
    public async Task<ApiResponse<long>> Handle(AppendEvaluateCommand request, CancellationToken ct)
    {
        var customerId = CustomerScope.Require(request.CustomerId);
        var evaluate = await _repo.GetByIdAsync(request.EvaluateId, ct).ConfigureAwait(false);

        // 评价不存在与「不是你的评价」都回「评价不存在」，不回「无权追评」：
        // 后者等于确认这个评价 Id 真实存在
        if (evaluate is null || evaluate.CustomerId != customerId)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.NotFound, "评价不存在");
        }

        // 首评被隐藏后不允许再追评：违规内容不该继续生长
        if (evaluate.IsHidden)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BusinessError, "该评价已被隐藏，无法追评");
        }

        var count = await _repo.CountAppendsAsync(evaluate.Id, ct).ConfigureAwait(false);
        if (count >= EvaluateCalculator.MaxAppends)
        {
            return ApiResults.Fail<long>(
                BaseApiResponseCode.BusinessError,
                new AppendLimitReachedException(EvaluateCalculator.MaxAppends).Message);
        }

        if (!EvaluateCalculator.IsAppendWindowOpen(evaluate.CreatedAt, DateTime.UtcNow))
        {
            return ApiResults.Fail<long>(
                BaseApiResponseCode.BusinessError, new AppendWindowExpiredException().Message);
        }

        var append = new EvaluateAppend
        {
            // 同 PublishEvaluateHandler：归属取命令里的值，不靠租户上下文反推
            CustomerId = customerId,
            EvaluateId = evaluate.Id,
            StarScore = request.StarScore,
            Content = (request.Content ?? string.Empty).Trim(),
            Images = EvaluateDtoFactory.JoinImages(request.Images)
        };

        var id = await _repo.InsertAppendAsync(append, ct).ConfigureAwait(false);

        // 追评**不重算均分**（规格 14.2）：挂一条追评就把店铺分数改掉，
        // 等于给了「先打 5 星再追评差评拉低」的刷分路径，也等于给了反向刷分路径
        return ApiResults.Ok(id, "追评成功");
    }
}
