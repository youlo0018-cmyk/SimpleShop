using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using Microsoft.Extensions.Logging;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;
using OrderService.Domain.Services;

namespace OrderService.Application.Features.Orders;

/// <summary>下单处理器：把命令转成编排请求，返回下单结果。</summary>
public sealed class CreateOrderHandler
    : MediatR.IRequestHandler<CreateOrderCommand, ApiResponse<OrderCreatedDto>>
{
    private readonly OrderCreator _creator;
    private readonly IOrderStore _store;

    /// <summary>构造处理器。</summary>
    /// <param name="creator">下单编排器。</param>
    /// <param name="store">落单端口，用于命中幂等时补齐金额展示字段。</param>
    public CreateOrderHandler(OrderCreator creator, IOrderStore store)
    {
        _creator = creator;
        _store = store;
    }

    /// <summary>执行下单。</summary>
    /// <param name="request">下单命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回订单号与实付金额。</returns>
    public async Task<ApiResponse<OrderCreatedDto>> Handle(CreateOrderCommand request, CancellationToken ct)
    {
        var lines = request.Lines
            .Select(a => new OrderLineRequest(
                a.SpuId, a.SkuId, a.Quantity,
                OrderAmountCalculator.Round2(a.UnitPrice),
                (a.ProductName ?? string.Empty).Trim(),
                (a.SkuSpecText ?? string.Empty).Trim(),
                a.DeliveryType))
            .ToArray();

        // 租户范围**以令牌为准**，不采信请求里的值。
        // 采信的话，商户管理员往请求体里塞一个别人的 merchantId 就能把单下到别家店铺名下，
        // 那时的库存、结算、对账全算到别人头上。
        var ctx = TenantContextHolder.Current;
        var platformId = ctx.PlatformId > 0 ? ctx.PlatformId : request.PlatformId;
        var merchantId = ctx.IsMerchant ? ctx.MerchantId : request.MerchantId;

        var outcome = await _creator.CreateAsync(new CreateOrderRequest(
            request.CustomerId, platformId, merchantId,
            request.IdempotencyKey.Trim(),
            request.ReceiverName.Trim(), request.ReceiverPhone.Trim(), request.ReceiverAddress.Trim(),
            lines,
            request.CouponId, request.PointsToUse, request.Remark.Trim(),
            new FreightRule(OrderAmountCalculator.Round2(request.Freight))), ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<OrderCreatedDto>(CodeFor(outcome.FailedStep), outcome.Error);
        }

        // 命中幂等时编排器只回订单号，金额要从库里再读一次；
        // 正常下单也可以直接读（刚写的，读的就是同一行），省得两份算法算出两个数。
        var order = await _store.FindByOrderNoAsync(outcome.OrderNo!, ct).ConfigureAwait(false);
        if (order is null)
        {
            return ApiResults.Fail<OrderCreatedDto>(
                BaseApiResponseCode.InternalError, "订单已创建但读取失败，请稍后在订单列表中查看");
        }

        var dto = new OrderCreatedDto(
            order.Id, order.OrderNo, order.Status, OrderStatusMachine.NameOf(order.Status),
            order.GoodsTotal, order.Freight, order.PointsDeduction, order.PayableAmount,
            order.CouponId, order.CouponDiscount, order.PointsUsed,
            outcome.AlreadyCreated);

        return ApiResults.Ok(dto, outcome.AlreadyCreated ? "该订单已提交过，直接返回原订单" : "下单成功");
    }

    /// <summary>按失败步骤挑一个贴切的响应码。</summary>
    /// <param name="failedStep">失败步骤，1~4。</param>
    /// <returns>响应码。</returns>
    /// <remarks>
    /// 库存不足必须是 <c>4001</c> 而不是笼统的 4000：前端据此提示
    /// 「商品库存不足，去逛逛别的吧」，而不是弹一个「操作失败，请重试」——
    /// 后者会让用户反复重试一件根本买不到的商品。
    /// </remarks>
    private static BaseApiResponseCode CodeFor(int failedStep)
        => failedStep == 3 ? BaseApiResponseCode.StockNotEnough : BaseApiResponseCode.BusinessError;
}

/// <summary>我的订单分页处理器。</summary>
public sealed class QueryMyOrdersHandler
    : MediatR.IRequestHandler<QueryMyOrdersCommand, ApiResponse<PagedResult<OrderListItemDto>>>
{
    private readonly IOrderStore _store;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    public QueryMyOrdersHandler(IOrderStore store) => _store = store;

    /// <summary>执行分页查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单分页结果。</returns>
    public async Task<ApiResponse<PagedResult<OrderListItemDto>>> Handle(
        QueryMyOrdersCommand request, CancellationToken ct)
    {
        var (orders, total) = await _store
            .ListByCustomerAsync(request.CustomerId, request.Status, request.Page, request.PageSize, ct)
            .ConfigureAwait(false);

        var aggregates = await _store
            .AggregateItemsAsync(orders.Select(a => a.Id).ToArray(), ct).ConfigureAwait(false);

        var items = orders.Select(a =>
        {
            aggregates.TryGetValue(a.Id, out var agg);
            return new OrderListItemDto(
                a.Id, a.OrderNo, a.Status, OrderStatusMachine.NameOf(a.Status),
                a.PayableAmount, agg.ItemQuantity,
                agg.FirstProductName ?? string.Empty,
                a.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                a.ReceiverName);
        }).ToList();

        return ApiResults.Ok(new PagedResult<OrderListItemDto>(
            items, total, request.Page, request.PageSize));
    }
}

/// <summary>订单详情处理器。</summary>
public sealed class QueryOrderDetailHandler
    : MediatR.IRequestHandler<QueryOrderDetailCommand, ApiResponse<OrderDetailDto>>
{
    private readonly IOrderStore _store;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    public QueryOrderDetailHandler(IOrderStore store) => _store = store;

    /// <summary>执行详情查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单详情，含全部订单行。</returns>
    public async Task<ApiResponse<OrderDetailDto>> Handle(QueryOrderDetailCommand request, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);

        // 订单不存在与「不是你的订单」都回 404：返回 403 等于告诉别人这个订单号真实存在，
        // 能被拿去枚举别人的订单号（TEST_CASES 6.2）。
        if (order is null || (request.CustomerId > 0 && order.CustomerId != request.CustomerId))
        {
            return ApiResults.Fail<OrderDetailDto>(BaseApiResponseCode.NotFound, "订单不存在");
        }

        var items = await _store.ListItemsAsync(order.Id, ct).ConfigureAwait(false);

        var dto = new OrderDetailDto(
            order.Id, order.OrderNo, order.Status, OrderStatusMachine.NameOf(order.Status),
            OrderStatusMachine.CanCancel(order.Status),
            OrderStatusMachine.CanConfirmReceipt(order.Status),
            order.GoodsTotal, order.Freight, order.PointsDeduction, order.PayableAmount,
            order.PointsUsed, order.CouponId, order.CouponDiscount,
            order.ReceiverName, order.ReceiverPhone, order.ReceiverAddress,
            order.Remark, order.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            items.Select(a => new OrderItemDto(
                a.SkuId, a.SpuId, a.ProductName, a.SkuSpecText,
                a.Price, a.Quantity, a.OriginalAmount,
                a.ActivityDiscount, a.CouponDiscount, a.PayableAmount, a.DeliveryType)).ToList());

        return ApiResults.Ok(dto);
    }
}

/// <summary>取消订单处理器。</summary>
/// <remarks>
/// 取消必须<b>先改状态再退占用</b>，不是反过来：反过来会出现两个请求都读到「待支付」、
/// 都去退了一遍库存，把别人的货也退了。改状态用条件更新，只有一个请求能成功。
/// </remarks>
public sealed class CancelOrderHandler : MediatR.IRequestHandler<CancelOrderCommand, ApiResponse>
{
    private readonly IOrderStore _store;
    private readonly IInventoryPort _inventory;
    private readonly IPointPort _points;
    private readonly ICouponPort _coupons;
    private readonly ILogger<CancelOrderHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="inventory">库存端口。</param>
    /// <param name="points">积分端口。</param>
    /// <param name="coupons">券端口。</param>
    /// <param name="logger">日志器。</param>
    public CancelOrderHandler(
        IOrderStore store, IInventoryPort inventory, IPointPort points,
        ICouponPort coupons, ILogger<CancelOrderHandler> logger)
    {
        _store = store;
        _inventory = inventory;
        _points = points;
        _coupons = coupons;
        _logger = logger;
    }

    /// <summary>执行取消。</summary>
    /// <param name="request">取消命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(CancelOrderCommand request, CancellationToken ct)
    {
        var orderNo = request.OrderNo.Trim();
        var order = await _store.FindByOrderNoAsync(orderNo, ct).ConfigureAwait(false);

        if (order is null || order.CustomerId != request.CustomerId)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "订单不存在");
        }

        if (!OrderStatusMachine.CanCancel(order.Status))
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.OrderStateInvalid,
                $"当前订单状态是「{OrderStatusMachine.NameOf(order.Status)}」，只有待支付的订单可以取消");
        }

        var affected = await _store
            .TryTransitStatusAsync(order.Id, OrderStatuses.PendingPayment, OrderStatuses.Cancelled, ct)
            .ConfigureAwait(false);

        if (affected == 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.OrderStateInvalid, "订单状态已变更，请刷新后重试");
        }

        await ReleaseOccupationsAsync(order, ct).ConfigureAwait(false);

        return ApiResponseFactory.Ok("订单已取消");
    }

    /// <summary>把该单占用的库存 / 积分 / 券都退回去。失败只记日志。</summary>
    /// <param name="order">已取消的订单。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 这一段失败<b>绝不能</b>反过来把「已取消」改成「取消失败」——
    /// 状态已经改掉了，订单对用户就是已取消；这时再抛错会让用户以为还能付款。
    /// 退不掉的资源交给补偿任务兜底，日志里能查到订单号。
    /// </remarks>
    private async Task ReleaseOccupationsAsync(Order order, CancellationToken ct)
    {
        var items = await _store.ListItemsAsync(order.Id, ct).ConfigureAwait(false);

        foreach (var item in items)
        {
            try
            {
                await _inventory.ReleaseAsync(item.SkuId, item.Quantity, $"{order.OrderNo}:{item.SkuId}", ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "取消订单回退库存失败：{OrderNo} SKU {SkuId}", order.OrderNo, item.SkuId);
            }
        }

        if (order.PointsUsed > 0)
        {
            try
            {
                await _points.UnfreezeAsync(order.CustomerId, order.OrderNo, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "取消订单解冻积分失败：{OrderNo}", order.OrderNo);
            }
        }

        if (order.CouponId > 0)
        {
            try
            {
                await _coupons.ReleaseAsync(order.CustomerId, order.OrderNo, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "取消订单回退券失败：{OrderNo}", order.OrderNo);
            }
        }
    }
}

/// <summary>确认收货处理器。</summary>
public sealed class ConfirmReceiptHandler : MediatR.IRequestHandler<ConfirmReceiptCommand, ApiResponse>
{
    private readonly IOrderStore _store;
    private readonly OrderCompletionReward _reward;
    private readonly ILogger<ConfirmReceiptHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="reward">完成奖励服务（发积分）。</param>
    /// <param name="logger">日志器。</param>
    public ConfirmReceiptHandler(
        IOrderStore store, OrderCompletionReward reward, ILogger<ConfirmReceiptHandler> logger)
    {
        _store = store;
        _reward = reward;
        _logger = logger;
    }

    /// <summary>执行确认收货。</summary>
    /// <param name="request">确认收货命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(ConfirmReceiptCommand request, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);

        if (order is null || order.CustomerId != request.CustomerId)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "订单不存在");
        }

        if (!OrderStatusMachine.CanConfirmReceipt(order.Status))
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.OrderStateInvalid,
                $"当前订单状态是「{OrderStatusMachine.NameOf(order.Status)}」，只有已发货的订单可以确认收货");
        }

        var affected = await _store
            .TryTransitStatusAsync(order.Id, OrderStatuses.PendingReceipt, OrderStatuses.Completed, ct)
            .ConfigureAwait(false);

        if (affected == 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.OrderStateInvalid, "订单状态已变更，请刷新后重试");
        }

        // 状态已改成 50 之后才发积分：反过来先发积分再改状态，
        // 改状态失败就会留下「积分发了但订单还没完成」的一笔账
        await _reward.GrantAsync(order, ct).ConfigureAwait(false);

        _logger.LogInformation("订单 {OrderNo} 确认收货完成，进入已完成", order.OrderNo);
        return ApiResponseFactory.Ok("已确认收货");
    }
}