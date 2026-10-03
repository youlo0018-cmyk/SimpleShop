using Collaboration.Domain.Common;
using MediatR;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;

namespace MarketingService.Application.Features.Seckill;

/// <summary>轮询抢购结果处理器。</summary>
/// <remarks>
/// 接口形状按规格保留（requestId + 轮询），即使当前是同步下单。
/// 将来把「同步调 OrderService」换成「发 MQ」时，前端一行都不用改。
/// </remarks>
public sealed class QueryGrabResultHandler
    : IRequestHandler<QueryGrabResultCommand, ApiResponse<GrabResultDto>>
{
    private readonly ISeckillRepository _seckill;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    public QueryGrabResultHandler(ISeckillRepository seckill) => _seckill = seckill;

    /// <summary>查询结果。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>抢购结果。</returns>
    /// <remarks>
    /// 别人的 requestId 一律按「查不到」处理，不回「无权查看」——
    /// requestId 是随机 GUID，回「无权」等于告诉对方「这个 ID 存在」，
    /// 而它不存在也返回一样的信息才对。
    /// </remarks>
    public async Task<ApiResponse<GrabResultDto>> Handle(
        QueryGrabResultCommand request, CancellationToken ct)
    {
        var grab = await _seckill.GetGrabByRequestIdAsync(request.RequestId, ct).ConfigureAwait(false);

        if (grab is null || grab.CustomerId != request.CustomerId)
        {
            return ApiResults.Fail<GrabResultDto>(BaseApiResponseCode.NotFound, "抢购记录不存在");
        }

        return ApiResults.Ok(new GrabResultDto(
            grab.RequestId, grab.ResultStatus, grab.ResultMessage, grab.OrderId, grab.OrderNo));
    }
}