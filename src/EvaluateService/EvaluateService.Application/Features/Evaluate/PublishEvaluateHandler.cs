using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using EvaluateService.Domain.Entities;
using EvaluateService.Domain.Exceptions;
using EvaluateService.Domain.IRepository;
using EvaluateService.Domain.Services;
using EvaluateService.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;
// `Features/Evaluate` 命名空间段遮蔽同名实体 `Evaluate`，见 CODING_STANDARD §6 第 1 条
using EvaluateEntity = EvaluateService.Domain.Entities.Evaluate;

namespace EvaluateService.Application.Features.Evaluate;

/// <summary>发表首评处理器。</summary>
/// <remarks>
/// 整条链路的顺序是「先信订单 → 再落评价」，每一步都可能拒：
/// 订单不存在 / 不是你的 / 没完成 / 这单没买过这个 SPU / 已经评过了。
/// 全部检查在插入之前完成，避免留下一条没有 SKU 标记的半截评价。
/// </remarks>
public sealed class PublishEvaluateHandler
    : IRequestHandler<PublishEvaluateCommand, ApiResponse<PublishEvaluateResult>>
{
    private readonly IEvaluateRepository _repo;
    private readonly IOrderPort _orders;
    private readonly IPointGrantClient _points;
    private readonly ILogger<PublishEvaluateHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="repo">评价仓储。</param>
    /// <param name="orders">订单服务端口。</param>
    /// <param name="points">积分端口，发表首评赠送 20 积分（BUSINESS.md 13.2）。</param>
    /// <param name="logger">日志器。</param>
    public PublishEvaluateHandler(
        IEvaluateRepository repo, IOrderPort orders,
        IPointGrantClient points, ILogger<PublishEvaluateHandler> logger)
    {
        _repo = repo;
        _orders = orders;
        _points = points;
        _logger = logger;
    }

    /// <summary>执行发表。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>评价 Id 与自动推导的 SKU 标记。</returns>
    public async Task<ApiResponse<PublishEvaluateResult>> Handle(
        PublishEvaluateCommand request, CancellationToken ct)
    {
        var customerId = CustomerScope.Require(request.CustomerId);
        var order = await _orders.GetForEvaluateAsync(request.OrderNo, ct).ConfigureAwait(false);

        // 订单不存在与「不是你的订单」都回「订单不存在」：
        // 回「无权评价」等于确认这个订单号真实存在，可被拿去枚举别人的订单号
        if (order is null || order.CustomerId != customerId)
        {
            return ApiResults.Fail<PublishEvaluateResult>(BaseApiResponseCode.NotFound, "订单不存在");
        }

        if (!order.CanEvaluate)
        {
            return ApiResults.Fail<PublishEvaluateResult>(
                BaseApiResponseCode.BusinessError, "订单完成后才能评价哦");
        }

        // SKU 标记**从订单推导**而不是让前端传：
        // 前端可能只传一个规格，也可能传一堆不属于这单的 SKU，
        // 那样「这条评价覆盖了哪些规格」就成了前端说了算
        var spu = order.Items.FirstOrDefault(a => a.SpuId == request.SpuId);
        if (spu is null || spu.Skus.Count == 0)
        {
            return ApiResults.Fail<PublishEvaluateResult>(
                BaseApiResponseCode.BusinessError, "该订单没有购买这个商品");
        }

        try
        {
            var specs = spu.Skus.Select(a => a.SkuSpecText).ToList();

            var evaluate = new EvaluateEntity
            {
                // 归属客户取命令里的值，**不靠租户上下文反推**：
                // 上下文只有走网关带令牌时才有值，服务被直接调用时是 0，
                // 那会把评价记到「客户 0」名下——「我的评价」查不到、追评被拒，
                // 而所有接口都返回成功。
                CustomerId = customerId,
                PlatformId = order.PlatformId,
                MerchantId = order.MerchantId,
                SpuId = request.SpuId,
                SpuName = spu.SpuName,
                SkuSpecs = EvaluateCalculator.FormatSpecs(specs),
                OrderNo = order.OrderNo,
                OrderId = order.OrderId,
                StarScore = request.StarScore,
                Content = (request.Content ?? string.Empty).Trim(),
                Images = EvaluateDtoFactory.JoinImages(request.Images),
                IsAnonymous = request.IsAnonymous
            };

            var refs = spu.Skus.Select(a => new EvaluateSkuRef
            {
                SkuId = a.SkuId,
                SkuSpecText = a.SkuSpecText,
                OrderItemId = a.OrderItemId
            }).ToList();

            var evaluateId = await _repo.InsertWithRefsAsync(evaluate, refs, ct).ConfigureAwait(false);

            // 发表首评赠送 20 积分（BUSINESS.md 13.2）。
            //
            // 幂等键用评价 Id：同一评价重复投递 / 重试只会发一次。
            // 发放失败**不让评价失败** —— 评价已经落库了，为了 20 积分把它回滚，
            // 用户看到的是「评价失败」却不知道为什么。记日志、由补偿任务补发。
            try
            {
                await _points.TryGrantEvaluateBonusAsync(customerId, evaluateId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "评价 {EvaluateId} 已发表，但赠送积分失败，需人工补发", evaluateId);
            }

            return ApiResults.Ok(
                new PublishEvaluateResult(evaluateId, request.SpuId, refs.Select(a => a.SkuId).ToList()),
                "评价成功，感谢您的反馈");
        }
        catch (DuplicateEvaluateException ex)
        {
            // 幂等命中：同一订单同一 SPU 只能有一条首评（规格 14.1）
            _logger.LogInformation(ex, "订单 {OrderNo} 对商品 {SpuId} 重复评价已被拦下",
                request.OrderNo, request.SpuId);

            return ApiResults.Fail<PublishEvaluateResult>(
                BaseApiResponseCode.BusinessError, "该商品您已经评价过了");
        }
    }
}
