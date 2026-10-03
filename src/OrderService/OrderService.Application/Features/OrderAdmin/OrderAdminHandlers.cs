using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using Collaboration.Domain.Entities;
using Microsoft.Extensions.Logging;
using OrderService.Application.Features.Orders;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;

namespace OrderService.Application.Features.OrderAdmin;

/// <summary>后台订单分页处理器。</summary>
public sealed class QueryAdminOrdersHandler
    : MediatR.IRequestHandler<QueryAdminOrdersCommand, ApiResponse<PagedResult<AdminOrderListItemDto>>>
{
    private readonly IOrderStore _store;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    public QueryAdminOrdersHandler(IOrderStore store) => _store = store;

    /// <summary>执行后台分页。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单分页结果。</returns>
    public async Task<ApiResponse<PagedResult<AdminOrderListItemDto>>> Handle(
        QueryAdminOrdersCommand request, CancellationToken ct)
    {
        // 租户范围取「命令里带的」与「上下文里的」两者的交集：
        // 商户管理员即使在请求体里硬塞别人的 MerchantId，也只能看到自己店铺的订单。
        // 超管两个都是 0，表示不限平台——这是唯一能看全部订单的角色。
        var ctx = TenantContextHolder.Current;
        var platformId = Narrow(request.PlatformId, ctx.PlatformId);
        var merchantId = Narrow(request.MerchantId, ctx.MerchantId);

        var (orders, total) = await _store.ListAsync(
            request.Status, request.Keyword, platformId, merchantId,
            request.Page, request.PageSize, ct).ConfigureAwait(false);

        var aggregates = await _store
            .AggregateItemsAsync(orders.Select(a => a.Id).ToArray(), ct).ConfigureAwait(false);

        var items = orders.Select(a =>
        {
            aggregates.TryGetValue(a.Id, out var agg);
            return new AdminOrderListItemDto(
                a.Id, a.OrderNo, a.CustomerId, a.Status, OrderStatusMachine.NameOf(a.Status),
                a.PayableAmount, agg.ItemQuantity,
                a.ReceiverName, a.ReceiverPhone,
                a.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        }).ToList();

        return ApiResults.Ok(new PagedResult<AdminOrderListItemDto>(
            items, total, request.Page, request.PageSize));
    }

    /// <summary>取两个范围里更窄的那个。</summary>
    /// <param name="requested">请求里带的范围，0 表示不限。</param>
    /// <param name="context">上下文里的范围，0 表示不限。</param>
    /// <returns>收窄后的范围。</returns>
    private static long Narrow(long requested, long context)
    {
        if (requested <= 0) return context;
        if (context <= 0) return requested;
        return requested == context ? requested : -1;   // -1 = 两者冲突，查询自然查不到任何行
    }
}

/// <summary>发货处理器（实物快递，20 → 30）。</summary>
/// <remarks>
/// 用户要求 D3：<b>手动点发货，不用填物流信息</b>。
/// 所以这里只有状态迁移，没有任何物流单号字段——后端刻意不提供那个字段，
/// 以免将来有人「顺手」把它加进查询接口，白送一个用户可见的物流轨迹入口。
/// </remarks>
public sealed class ShipOrderHandler : MediatR.IRequestHandler<ShipOrderCommand, ApiResponse>
{
    private readonly IOrderStore _store;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    public ShipOrderHandler(IOrderStore store) => _store = store;

    /// <summary>执行发货。</summary>
    /// <param name="request">发货命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// <b>重复发货按幂等处理</b>（TEST_CASES UT-ORD-014）：已经处于待收货 / 已完成的单
    /// 再点一次发货，回「已发货」而不是报错。运营在列表上误点两下是常事，
    /// 回一个红色报错会让人以为货没发出去、于是再点第三次。
    /// 真正的防重复靠状态迁移本身——条件更新只可能生效一次。
    /// </remarks>
    public async Task<ApiResponse> Handle(ShipOrderCommand request, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);
        if (order is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "订单不存在");

        if (order.Status is Domain.Entities.OrderStatuses.PendingReceipt or Domain.Entities.OrderStatuses.Completed)
        {
            return ApiResponseFactory.Ok("该订单已发货");
        }

        if (order.Status != Domain.Entities.OrderStatuses.PendingShipment)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.OrderStateInvalid,
                $"当前订单状态是「{OrderStatusMachine.NameOf(order.Status)}」，只有待发货的订单可以发货");
        }

        var affected = await _store.TryTransitStatusAsync(
            order.Id,
            Domain.Entities.OrderStatuses.PendingShipment,
            Domain.Entities.OrderStatuses.PendingReceipt, ct).ConfigureAwait(false);

        if (affected == 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.OrderStateInvalid, "订单状态已变更，请刷新后重试");
        }

        return ApiResponseFactory.Ok("已发货");
    }
}

/// <summary>虚拟商品发货处理器（20 → 50，发货即完成）。</summary>
/// <remarks>
/// 虚拟订单不走物流也不走自提：发货这一步就是终点。
/// 顺带满足用户的要求「D4 上一轮只针对虚拟订单」——虚拟订单同样不许退款，
/// 退款校验在 <see cref="RefundOrderHandler"/> 里按配送方式拦住。
/// </remarks>
public sealed class DeliverVirtualHandler : MediatR.IRequestHandler<DeliverVirtualCommand, ApiResponse>
{
    private readonly IOrderStore _store;
    private readonly OrderCompletionReward _reward;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="reward">完成奖励服务（发积分）。</param>
    public DeliverVirtualHandler(IOrderStore store, OrderCompletionReward reward)
    {
        _store = store;
        _reward = reward;
    }

    /// <summary>执行虚拟发货。</summary>
    /// <param name="request">发货命令，Remark 一般放卡号 / 激活码。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(DeliverVirtualCommand request, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);
        if (order is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "订单不存在");

        var items = await _store.ListItemsAsync(order.Id, ct).ConfigureAwait(false);
        var hasPhysical = items.Any(a => a.DeliveryType != DeliveryTypes.Virtual);

        // 「这单到底是不是虚拟单」必须**先**判，不能因为状态已是 50 就走幂等分支放行：
        // 否则对一件实物订单误点「虚拟发货」，也会得到一个「已发货并完成」的成功回执，
        // 运营以为虚拟发货走通了，而实物其实还躺着没发。
        if (hasPhysical)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BusinessError,
                "该订单包含实物商品，请使用「发货」而不是「虚拟发货」");
        }

        // 幂等：这单确实是虚拟单且已经完成，说明之前已经发过了
        if (order.Status == Domain.Entities.OrderStatuses.Completed)
        {
            return ApiResponseFactory.Ok("该订单已发货并完成");
        }

        var affected = await _store.TryTransitStatusAsync(
            order.Id,
            Domain.Entities.OrderStatuses.PendingShipment,
            Domain.Entities.OrderStatuses.Completed, ct).ConfigureAwait(false);

        if (affected == 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.OrderStateInvalid, "订单状态已变更，请刷新后重试");
        }

        // 虚拟发货即完成，所以这里是三条进「已完成」的路之一，积分同样要发
        await _reward.GrantAsync(order, ct).ConfigureAwait(false);

        return ApiResponseFactory.Ok("已发货并完成");
    }
}

/// <summary>自提备货完成处理器（20 → 40，并生成取货码）。</summary>
public sealed class SelfPickupReadyHandler
    : MediatR.IRequestHandler<SelfPickupReadyCommand, ApiResponse<PickupCodeDto>>
{
    private readonly IOrderStore _store;
    private readonly IPickupCodeCodec _codec;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="codec">取货码编解码。</param>
    public SelfPickupReadyHandler(IOrderStore store, IPickupCodeCodec codec)
    {
        _store = store;
        _codec = codec;
    }

    /// <summary>执行备货完成。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回取货码。</returns>
    public async Task<ApiResponse<PickupCodeDto>> Handle(SelfPickupReadyCommand request, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);
        if (order is null) return ApiResults.Fail<PickupCodeDto>(BaseApiResponseCode.NotFound, "订单不存在");

        // 幂等：已经备好过就把同一个取货码再给一次，运营不必去翻聊天记录找上一次的结果
        if (order.Status == Domain.Entities.OrderStatuses.PendingPickup)
        {
            return ApiResults.Ok(
                new PickupCodeDto(order.OrderNo, _codec.Encrypt(order.OrderNo),
                    Domain.Entities.OrderStatuses.PendingPickup,
                    OrderStatusMachine.NameOf(Domain.Entities.OrderStatuses.PendingPickup),
                    Verified: false),
                "该订单已备货，取货码如下");
        }

        var items = await _store.ListItemsAsync(order.Id, ct).ConfigureAwait(false);
        if (!items.Any(a => a.DeliveryType == DeliveryTypes.SelfPickup))
        {
            return ApiResults.Fail<PickupCodeDto>(
                BaseApiResponseCode.BusinessError, "该订单没有自提商品，不需要备货取货");
        }

        if (order.Status != Domain.Entities.OrderStatuses.PendingShipment)
        {
            return ApiResults.Fail<PickupCodeDto>(
                BaseApiResponseCode.OrderStateInvalid,
                $"当前订单状态是「{OrderStatusMachine.NameOf(order.Status)}」，只有待发货的订单可以备货");
        }

        var affected = await _store.TryTransitStatusAsync(
            order.Id,
            Domain.Entities.OrderStatuses.PendingShipment,
            Domain.Entities.OrderStatuses.PendingPickup, ct).ConfigureAwait(false);

        if (affected == 0)
        {
            return ApiResults.Fail<PickupCodeDto>(BaseApiResponseCode.OrderStateInvalid, "订单状态已变更，请刷新后重试");
        }

        var code = _codec.Encrypt(order.OrderNo);

        return ApiResults.Ok(
            new PickupCodeDto(order.OrderNo, code,
                Domain.Entities.OrderStatuses.PendingPickup,
                OrderStatusMachine.NameOf(Domain.Entities.OrderStatuses.PendingPickup),
                Verified: false),
            "备货完成，请把取货码交给顾客");
    }
}

/// <summary>核销取货码处理器（40 → 50）。</summary>
public sealed class VerifyPickupCodeHandler
    : MediatR.IRequestHandler<VerifyPickupCodeCommand, ApiResponse<PickupCodeDto>>
{
    private readonly IOrderStore _store;
    private readonly IPickupCodeCodec _codec;
    private readonly OrderCompletionReward _reward;
    private readonly ILogger<VerifyPickupCodeHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="codec">取货码编解码。</param>
    /// <param name="reward">完成奖励服务（发积分）。</param>
    /// <param name="logger">日志器。</param>
    public VerifyPickupCodeHandler(
        IOrderStore store, IPickupCodeCodec codec,
        OrderCompletionReward reward, ILogger<VerifyPickupCodeHandler> logger)
    {
        _store = store;
        _codec = codec;
        _reward = reward;
        _logger = logger;
    }

    /// <summary>执行核销。</summary>
    /// <param name="request">核销命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回订单号与新的取货码（已作废，仅供回显）。</returns>
    public async Task<ApiResponse<PickupCodeDto>> Handle(VerifyPickupCodeCommand request, CancellationToken ct)
    {
        if (!_codec.TryDecrypt(request.PickupCode, out var orderNo) || string.IsNullOrWhiteSpace(orderNo))
        {
            return ApiResults.Fail<PickupCodeDto>(
                BaseApiResponseCode.BadRequest, "取货码无效，请让顾客重新出示");
        }

        var order = await _store.FindByOrderNoAsync(orderNo!, ct).ConfigureAwait(false);
        if (order is null)
        {
            return ApiResults.Fail<PickupCodeDto>(BaseApiResponseCode.NotFound, "取货码对应的订单不存在");
        }

        // 归属校验：不是本平台 / 本商户的码不能核销。
        // 不核销只是拒了这一单，不该告诉对方「这单确实存在」——那等于订单号探测。
        if (!BelongsToTenant(order, request.PlatformId, request.MerchantId))
        {
            _logger.LogWarning("商户 {MerchantId} 试图核销不属于自己平台的取货码，订单 {OrderNo}",
                request.MerchantId, order.OrderNo);
            return ApiResults.Fail<PickupCodeDto>(
                BaseApiResponseCode.NotFound, "取货码无效，请让顾客重新出示");
        }

        if (!OrderStatusMachine.CanVerifyPickup(order.Status))
        {
            return ApiResults.Fail<PickupCodeDto>(
                BaseApiResponseCode.OrderStateInvalid,
                $"当前订单状态是「{OrderStatusMachine.NameOf(order.Status)}」，只有待取货的订单可以核销");
        }

        var affected = await _store.TryTransitStatusAsync(
            order.Id,
            Domain.Entities.OrderStatuses.PendingPickup,
            Domain.Entities.OrderStatuses.Completed, ct).ConfigureAwait(false);

        if (affected == 0)
        {
            return ApiResults.Fail<PickupCodeDto>(BaseApiResponseCode.OrderStateInvalid, "订单状态已变更，请刷新后重试");
        }

        // 取货核销也是一条进「已完成」的路
        await _reward.GrantAsync(order, ct).ConfigureAwait(false);

        _logger.LogInformation("取货码核销成功，订单 {OrderNo} 已完成", order.OrderNo);

        return ApiResults.Ok(new PickupCodeDto(
            order.OrderNo, _codec.Encrypt(order.OrderNo),
            Domain.Entities.OrderStatuses.Completed,
            OrderStatusMachine.NameOf(Domain.Entities.OrderStatuses.Completed),
            Verified: true), "核销成功，订单已完成");
    }

    /// <summary>订单是否属于指定平台 / 商户。</summary>
    /// <param name="order">订单。</param>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="merchantId">商户 Id，0 表示不限。</param>
    /// <returns>属于返回 true。</returns>
    private static bool BelongsToTenant(Order order, long platformId, long merchantId)
        => (platformId <= 0 || order.PlatformId == platformId)
           && (merchantId <= 0 || order.MerchantId == merchantId);
}

/// <summary>模拟支付处理器（仅测试环境）。</summary>
public sealed class SimulatePaymentHandler
    : MediatR.IRequestHandler<SimulatePaymentCommand, ApiResponse<SimulatePaymentDto>>
{
    private readonly IOrderStore _store;
    private readonly OrderPaymentCompleter _completer;
    private readonly ILogger<SimulatePaymentHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="completer">支付收尾服务。</param>
    /// <param name="logger">日志器。</param>
    public SimulatePaymentHandler(
        IOrderStore store, OrderPaymentCompleter completer, ILogger<SimulatePaymentHandler> logger)
    {
        _store = store;
        _completer = completer;
        _logger = logger;
    }

    /// <summary>执行模拟支付。</summary>
    /// <param name="request">命令，Succeed 决定模拟结果。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>模拟支付结果。</returns>
    /// <remarks>
    /// <b>失败分支刻意什么都不做</b>：真实支付失败时订单本来就还是 10 待支付，
    /// 库存还锁着、积分还冻着、券还占着，用户可以重新发起支付。
    /// 模拟失败如果顺手把这些退了，那测试出来的行为跟真实支付完全相反。
    /// </remarks>
    public async Task<ApiResponse<SimulatePaymentDto>> Handle(SimulatePaymentCommand request, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);
        if (order is null)
        {
            return ApiResults.Fail<SimulatePaymentDto>(BaseApiResponseCode.NotFound, "订单不存在");
        }

        if (!request.Succeed)
        {
            _logger.LogInformation("模拟支付失败：订单 {OrderNo}，状态保持不变", order.OrderNo);

            return ApiResults.Ok(new SimulatePaymentDto(
                order.OrderNo, false, order.Status, OrderStatusMachine.NameOf(order.Status),
                "已模拟支付失败，订单状态保持不变，可重新发起支付"), "模拟支付失败");
        }

        var outcome = await _completer.CompleteAsync(order, ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<SimulatePaymentDto>(
                outcome.FailedStep == 1 ? BaseApiResponseCode.StockNotEnough : BaseApiResponseCode.BusinessError,
                outcome.Error);
        }

        var current = await _store.FindByOrderNoAsync(order.OrderNo, ct).ConfigureAwait(false);
        var status = current?.Status ?? order.Status;

        var message = outcome.AlreadyCompleted
            ? "该订单已经支付过了，未重复处理"
            : "模拟支付成功";

        return ApiResults.Ok(new SimulatePaymentDto(
            order.OrderNo, true, status, OrderStatusMachine.NameOf(status), message), message);
    }
}

/// <summary>退款处理器。</summary>
/// <remarks>
/// 退款是全系统第二复杂的补偿链路，这里先落地订单状态侧与库存回补：
/// 积分按比例回收、券退回属于 PaymentService 的职责（BUSINESS.md 10），
/// 本轮不重复实现，避免同一笔账被两个服务各记一次。
/// </remarks>
public sealed class RefundOrderHandler : MediatR.IRequestHandler<RefundOrderCommand, ApiResponse>
{
    private readonly IOrderStore _store;
    private readonly IInventoryPort _inventory;
    private readonly ILogger<RefundOrderHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="inventory">库存端口。</param>
    /// <param name="logger">日志器。</param>
    public RefundOrderHandler(
        IOrderStore store, IInventoryPort inventory, ILogger<RefundOrderHandler> logger)
    {
        _store = store;
        _inventory = inventory;
        _logger = logger;
    }

    /// <summary>执行退款。</summary>
    /// <param name="request">退款命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(RefundOrderCommand request, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);
        if (order is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "订单不存在");

        var items = await _store.ListItemsAsync(order.Id, ct).ConfigureAwait(false);

        if (items.Any(a => a.DeliveryType == DeliveryTypes.Virtual))
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BusinessError, "虚拟商品订单不支持退款");
        }

        if (!OrderStatusMachine.CanRefund(order.Status, items.Select(a => a.DeliveryType)))
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.OrderStateInvalid,
                $"当前订单状态是「{OrderStatusMachine.NameOf(order.Status)}」，已完成或已退款的订单不能再退");
        }

        var affected = await _store.TryTransitStatusAsync(
            order.Id, order.Status, Domain.Entities.OrderStatuses.Refunded, ct).ConfigureAwait(false);

        if (affected == 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.OrderStateInvalid, "订单状态已变更，请刷新后重试");
        }

        // 回补库存：未发货（10 / 20）的货还锁着，要 release；已发货（30 / 40）的货已经扣减，要 replenish。
        // 分不清这两种就会把 locked 减成负数，或者 deducted 减成负数——两边都靠数据库非负约束挡住，
        // 结果是退款「失败」，而实际上钱已经退了。
        var lockedPhase = order.Status is Domain.Entities.OrderStatuses.PendingPayment
            or Domain.Entities.OrderStatuses.PendingShipment;

        foreach (var item in items)
        {
            try
            {
                if (lockedPhase)
                {
                    await _inventory.ReleaseAsync(
                        item.SkuId, item.Quantity, $"{order.OrderNo}:{item.SkuId}", ct).ConfigureAwait(false);
                }
                else
                {
                    await _inventory.ReplenishAsync(
                        item.SkuId, item.Quantity, $"{order.OrderNo}:{item.SkuId}", ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "退款回补库存失败：订单 {OrderNo} SKU {SkuId}", order.OrderNo, item.SkuId);
            }
        }

        _logger.LogInformation("订单 {OrderNo} 已退款：{Reason}", order.OrderNo, request.Remark);
        return ApiResponseFactory.Ok("退款成功");
    }
}