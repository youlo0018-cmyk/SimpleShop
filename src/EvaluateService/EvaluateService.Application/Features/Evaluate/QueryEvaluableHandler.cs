using Collaboration.Domain.Common;
using EvaluateService.Domain.IRepository;
using EvaluateService.Domain.Services;
using EvaluateService.Application.Services;
using MediatR;

namespace EvaluateService.Application.Features.Evaluate;

/// <summary>可评价商品查询处理器。</summary>
/// <remarks>
/// 「已完成订单里还没评过的商品」是跨服务的判断：
/// 「哪些订单已完成」只有订单服务知道，「哪些已经评过」只有本服务知道，
/// 所以这里**只支持按订单号查**（<c>OrderNo</c> 传空则提示先选订单），
/// 不做「扫全量已完成订单」——那要订单服务提供分页扫描接口，
/// 而实际入口永远是「我的订单 → 点某一单 → 去评价」，本来就带着订单号。
/// </remarks>
public sealed class QueryEvaluableHandler
    : IRequestHandler<QueryEvaluableCommand, ApiResponse<List<EvaluableItemDto>>>
{
    private readonly IEvaluateRepository _repo;
    private readonly IOrderPort _orders;

    /// <summary>构造处理器。</summary>
    /// <param name="repo">评价仓储。</param>
    /// <param name="orders">订单服务端口。</param>
    public QueryEvaluableHandler(IEvaluateRepository repo, IOrderPort orders)
    {
        _repo = repo;
        _orders = orders;
    }

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>该订单下每个 SPU 的评价状态。</returns>
    public async Task<ApiResponse<List<EvaluableItemDto>>> Handle(
        QueryEvaluableCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OrderNo))
        {
            return ApiResults.Fail<List<EvaluableItemDto>>(
                BaseApiResponseCode.BadRequest, "请先选择要评价的订单");
        }

        var order = await _orders.GetForEvaluateAsync(request.OrderNo, ct).ConfigureAwait(false);
        if (order is null || order.CustomerId != request.CustomerId)
        {
            return ApiResults.Fail<List<EvaluableItemDto>>(BaseApiResponseCode.NotFound, "订单不存在");
        }

        if (!order.CanEvaluate)
        {
            return ApiResults.Fail<List<EvaluableItemDto>>(
                BaseApiResponseCode.BusinessError, "订单完成后才能评价哦");
        }

        var evaluated = await _repo.GetEvaluatedSpuIdsAsync(order.OrderNo, ct).ConfigureAwait(false);

        var result = order.Items.Select(spu => new EvaluableItemDto(
            order.OrderNo,
            spu.SpuId,
            spu.SpuName,
            EvaluateCalculator.FormatSpecs(spu.Skus.Select(a => a.SkuSpecText).ToList()),
            MainImage: string.Empty,
            Evaluated: evaluated.Contains(spu.SpuId))).ToList();

        return ApiResults.Ok(result);
    }
}
