using Collaboration.Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;
using OrderService.Domain.Services;

namespace OrderService.Application.Features.Orders;

/// <summary>秒杀下单处理器。</summary>
/// <remarks>
/// 刻意复用 <see cref="OrderCreator"/> 的完整编排（客户锁 → 占券 → 锁积分 → 落单），
/// 只把 ③ 锁库存整步跳过。理由是秒杀单除了库存语义不同，其余完全就是一张普通订单：
/// 要用券、要用积分、要幂等、要走同样的补偿链路。
/// 另起一套编排等于把这些规则复制一遍，改一处漏一处。
/// </remarks>
public sealed class CreateSeckillOrderHandler
    : IRequestHandler<CreateSeckillOrderCommand, ApiResponse<OrderCreatedDto>>
{
    private readonly OrderCreator _creator;
    private readonly IOrderStore _store;
    private readonly ILogger<CreateSeckillOrderHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="creator">下单编排器。</param>
    /// <param name="store">落单端口，用于回读订单金额。</param>
    /// <param name="logger">日志器。</param>
    public CreateSeckillOrderHandler(
        OrderCreator creator, IOrderStore store, ILogger<CreateSeckillOrderHandler> logger)
    {
        _creator = creator;
        _store = store;
        _logger = logger;
    }

    /// <summary>执行秒杀下单。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回订单号与实付金额。</returns>
    public async Task<ApiResponse<OrderCreatedDto>> Handle(
        CreateSeckillOrderCommand request, CancellationToken ct)
    {
        // 单行：一次抢购就是一件商品，这是限购决定的
        var line = new OrderLineRequest(
            request.SpuId, request.SkuId, request.Quantity,
            Math.Round(request.SeckillPrice, 2, MidpointRounding.AwayFromZero),
            request.ProductName, request.SkuSpecText, request.DeliveryType,
            OrderSourceTypes.Seckill);

        var outcome = await _creator.CreateAsync(new CreateOrderRequest(
            request.CustomerId, request.PlatformId, request.MerchantId,
            request.IdempotencyKey,
            request.ReceiverName, request.ReceiverPhone, request.ReceiverAddress,
            new[] { line },
            request.CouponId, request.PointsToUse, request.Remark,
            new FreightRule(Math.Round(request.Freight, 2, MidpointRounding.AwayFromZero)),

            // 🔴 秒杀的关键标志：库存在发布场次时已划走，这里不再锁常规库存
            InventoryPreDeducted: true), ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            _logger.LogWarning("秒杀下单失败：客户 {CustomerId} 商品 {SkuId} 步骤 {Step} {Error}",
                request.CustomerId, request.SkuId, outcome.FailedStep, outcome.Error);

            return ApiResults.Fail<OrderCreatedDto>(
                outcome.FailedStep == 3 ? BaseApiResponseCode.StockNotEnough : BaseApiResponseCode.BusinessError,
                outcome.Error);
        }

        var order = await _store.FindByOrderNoAsync(outcome.OrderNo!, ct).ConfigureAwait(false);
        if (order is null)
        {
            return ApiResults.Fail<OrderCreatedDto>(
                BaseApiResponseCode.InternalError, "订单已创建但读取失败，请在订单列表中查看");
        }

        return ApiResults.Ok(new OrderCreatedDto(
            order.Id, order.OrderNo, order.Status, OrderStatusMachine.NameOf(order.Status),
            order.GoodsTotal, order.Freight, order.PointsDeduction, order.PayableAmount,
            order.CouponId, order.CouponDiscount, order.PointsUsed, outcome.AlreadyCreated),
            outcome.AlreadyCreated ? "该订单已提交过，直接返回原订单" : "抢购成功");
    }
}