using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using Collaboration.Domain.Entities;
using Microsoft.Extensions.Logging;
using OrderService.Application.Features.Orders;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;
using OrderService.Domain.Services;

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
            request.CustomerId, request.CustomerNo?.Trim() ?? string.Empty,
            request.From, request.To,
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
                a.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                agg.HasPhysical, agg.HasVirtual, agg.HasSelfPickup,
                a.CustomerNo, a.MerchantId);
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

/// <summary>后台订单详情处理器。</summary>
public sealed class QueryAdminOrderDetailHandler
    : MediatR.IRequestHandler<QueryAdminOrderDetailCommand, ApiResponse<OrderDetailDto>>
{
    private readonly IOrderStore _store;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    public QueryAdminOrderDetailHandler(IOrderStore store) => _store = store;

    /// <summary>执行详情查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单详情，含全部订单行。</returns>
    /// <remarks>
    /// 与 C 端详情**刻意不同**：这里不校验客户归属。
    /// 后台的可见范围由网关的租户上下文 + 权限点决定；
    /// 归属校验回答的是「这笔单是不是你的」，那是 C 端的事。
    /// 搬到后台来的话，后台永远查不到别人的单，运营会以为数据丢了。
    /// </remarks>
    public async Task<ApiResponse<OrderDetailDto>> Handle(
        QueryAdminOrderDetailCommand request, CancellationToken ct)
    {
        var order = await _store.GetByIdAsync(request.OrderId, ct).ConfigureAwait(false);

        if (order is null)
        {
            return ApiResults.Fail<OrderDetailDto>(BaseApiResponseCode.NotFound, "订单不存在");
        }

        var items = await _store.ListItemsAsync(order.Id, ct).ConfigureAwait(false);

        // 后台要展示「每行还能退多少」，所以把行级已退余额一起算出来。
        // C 端不需要这个，所以不塞进共用组装器 —— 否则 C 端每次查详情都多一条聚合查询。
        var refunded = await _store
            .AggregateRefundedItemsAsync(order.Id, ct).ConfigureAwait(false);

        var itemDtos = items.Select(a =>
        {
            refunded.TryGetValue(a.Id, out var r);
            return new OrderItemDto(
                a.Id, a.SkuId, a.SpuId, a.ProductName, a.SkuSpecText,
                a.Price, a.Quantity, a.OriginalAmount,
                a.ActivityDiscount, a.CouponDiscount, a.PayableAmount, a.DeliveryType,
                a.SourceType,
                r.Quantity,
                Math.Max(0m, decimal.Round(
                    a.PayableAmount - r.Amount, 2, MidpointRounding.AwayFromZero)));
        }).ToList();

        return ApiResults.Ok(
            OrderDetailAssembler.Build(order, items) with { Items = itemDtos });
    }
}

/// <summary>后台代客取消处理器。</summary>
public sealed class AdminCancelOrderHandler : MediatR.IRequestHandler<AdminCancelOrderCommand, ApiResponse>
{
    private readonly IOrderStore _store;
    private readonly OrderCancellationService _cancellation;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="cancellation">取消服务。</param>
    public AdminCancelOrderHandler(IOrderStore store, OrderCancellationService cancellation)
    {
        _store = store;
        _cancellation = cancellation;
    }

    /// <summary>执行取消。</summary>
    /// <param name="request">取消命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>取消结果。</returns>
    public async Task<ApiResponse> Handle(AdminCancelOrderCommand request, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);
        if (order is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "订单不存在");

        if (!OrderStatusMachine.CanCancel(order.Status))
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.OrderStateInvalid,
                $"当前订单状态是「{OrderStatusMachine.NameOf(order.Status)}」，只有待支付的订单可以取消");
        }

        var result = await _cancellation.CancelAsync(order, request.Remark, ct).ConfigureAwait(false);
        return result.Changed
            ? ApiResponseFactory.Ok("订单已取消")
            : ApiResponseFactory.Fail(BaseApiResponseCode.OrderStateInvalid, "订单状态已变更，请刷新后重试");
    }
}

/// <summary>发货处理器（实物快递，20 → 30），同时写入物流公司与运单号。</summary>
/// <remarks>
/// 早期版本按用户要求「手动点发货、不填物流信息」，当时刻意没有任何物流字段。
/// 现在改为<b>必须选择物流公司并录入运单号</b>：客服接到物流异常时，
/// 没有单号的订单无从追责，「已发货」只是一个空口的状态而已。
///
/// <para>公司名<b>不采信前端传值</b>，而是由字典的归属方（商品服务）给出权威名称：
/// 前端传的名字可以随便编，订单上就会留下一条查无此公司的物流记录。</para>
/// </remarks>
public sealed class ShipOrderHandler : MediatR.IRequestHandler<ShipOrderCommand, ApiResponse>
{
    private readonly IOrderStore _store;
    private readonly ILogisticsCompanyPort _logistics;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="logistics">物流公司查询端口。</param>
    public ShipOrderHandler(IOrderStore store, ILogisticsCompanyPort logistics)
    {
        _store = store;
        _logistics = logistics;
    }

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

        // 字典里查不到就把请求挡在这里，而不是发出去之后订单上挂一个空公司名。
        var companyName = await _logistics
            .ResolveNameAsync(request.LogisticsCompanyId, ct).ConfigureAwait(false);

        if (companyName is null)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.NotFound,
                "物流公司不存在，可能已被删除，请刷新后重新选择");
        }

        var trackingNo = request.TrackingNo.Trim();
        var affected = await _store.TryShipAsync(
            order.Id,
            Domain.Entities.OrderStatuses.PendingShipment,
            Domain.Entities.OrderStatuses.PendingReceipt,
            request.LogisticsCompanyId,
            companyName,
            trackingNo,
            DateTime.UtcNow,
            ct).ConfigureAwait(false);

        if (affected == 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.OrderStateInvalid, "订单状态已变更，请刷新后重试");
        }

        return ApiResponseFactory.Ok($"已发货 · {companyName} {trackingNo}");
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
            Domain.Entities.OrderStatuses.Completed,
            completedAt: DateTime.UtcNow, ct: ct).ConfigureAwait(false);

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

/// <summary>退款处理器，支持多次部分退款。</summary>
/// <remarks>
/// 退款是全系统第二复杂的补偿链路，这里先落地订单状态侧与库存回补：
/// 积分按比例回收、券退回属于 PaymentService 的职责（BUSINESS.md 10），
/// 本轮不重复实现，避免同一笔账被两个服务各记一次。
///
/// <para><b>早期实现是一退就把订单打成 60 已退款</b>，于是第二次退款无处落脚：
/// 「一件退掉了、另一件还想退」这种最常见的诉求完全做不了。
/// 现在一笔订单可以有多条退款记录（<see cref="OrderRefund"/>），
/// 退完剩余余额才把订单置为已退款。</para>
/// </remarks>
public sealed class RefundOrderHandler
    : MediatR.IRequestHandler<RefundOrderCommand, ApiResponse<RefundResultDto>>
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
    /// <returns>退款结果，含本次金额、累计已退与剩余可退。</returns>
    public async Task<ApiResponse<RefundResultDto>> Handle(
        RefundOrderCommand request, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);
        if (order is null)
        {
            return ApiResults.Fail<RefundResultDto>(BaseApiResponseCode.NotFound, "订单不存在");
        }

        var items = await _store.ListItemsAsync(order.Id, ct).ConfigureAwait(false);

        if (items.Any(a => a.DeliveryType == DeliveryTypes.Virtual))
        {
            return ApiResults.Fail<RefundResultDto>(
                BaseApiResponseCode.BusinessError, "虚拟商品订单不支持退款");
        }

        if (!OrderStatusMachine.CanRefund(order.Status, items.Select(a => a.DeliveryType)))
        {
            return ApiResults.Fail<RefundResultDto>(
                BaseApiResponseCode.OrderStateInvalid,
                $"当前订单状态是「{OrderStatusMachine.NameOf(order.Status)}」，已完成或已退款的订单不能再退");
        }

        // 已退余额按行聚合，一次查完而不是每行查一次（一张单最多 50 行）
        var refundedByLine = await _store
            .AggregateRefundedItemsAsync(order.Id, ct).ConfigureAwait(false);

        var refundable = items
            .Select(a => refundedByLine.TryGetValue(a.Id, out var r)
                ? new RefundableLine(a.Id, a.Quantity, r.Quantity, a.PayableAmount, r.Amount)
                : new RefundableLine(a.Id, a.Quantity, 0, a.PayableAmount, 0m))
            .ToList();

        var requests = request.Lines?
            .Select(a => new RefundLineRequest(a.OrderItemId, a.Quantity, a.Amount))
            .ToList();

        var resolution = OrderRefundRules.Resolve(
            refundable, order.PayableAmount, order.RefundedAmount, requests);

        if (!resolution.IsValid)
        {
            return ApiResults.Fail<RefundResultDto>(BaseApiResponseCode.BusinessError, resolution.Error);
        }

        var ctx = TenantContextHolder.Current;
        var refundNo = BuildRefundNo();

        var refund = new OrderRefund
        {
            RefundNo = refundNo,
            OrderId = order.Id,
            OrderNo = order.OrderNo,
            PlatformId = order.PlatformId,
            MerchantId = order.MerchantId,
            CustomerId = order.CustomerId,
            Amount = resolution.Total,
            // 退完剩余余额才算整单退，否则是部分退
            RefundType = resolution.FullyRefunded
                ? OrderRefundTypes.Whole
                : OrderRefundTypes.Partial,
            FullyRefunded = resolution.FullyRefunded,
            Reason = request.Remark.Trim(),
            OperatorId = ctx.UserId,
            OperatorName = ctx.UserName
        };

        var detail = resolution.Lines.Select(a =>
        {
            // OrderItemId = 0 是「运费与优惠分摊」伪行：它不对应任何商品，
            // 所以只留金额，不参与库存回补。
            var line = a.OrderItemId == 0 ? null : items.First(b => b.Id == a.OrderItemId);
            return new OrderRefundItem
            {
                OrderItemId = a.OrderItemId,
                SkuId = line?.SkuId ?? 0,
                ProductName = line?.ProductName ?? "运费与优惠分摊",
                SkuSpecText = line?.SkuSpecText ?? string.Empty,
                Quantity = a.Quantity,
                Amount = a.Amount
            };
        }).ToList();

        var refundId = await _store.SaveRefundAsync(refund, detail, ct).ConfigureAwait(false);

        // 🔴 余额判断在 SQL 的 WHERE 里（refunded_amount + 本次 <= payable_amount），
        // 所以受影响行数为 0 就是「余额已被别人用掉」或「状态已变」。
        var affected = await _store.TryApplyRefundAsync(
            order.Id, order.Status, resolution.Total, resolution.FullyRefunded, ct)
            .ConfigureAwait(false);

        if (affected == 0)
        {
            _logger.LogWarning(
                "订单 {OrderNo} 退款 {RefundNo} 未生效（余额不足或状态已变）", order.OrderNo, refundNo);

            return ApiResults.Fail<RefundResultDto>(
                BaseApiResponseCode.OrderStateInvalid,
                "订单可退余额不足或状态已变更，请刷新后重试");
        }

        // 回补库存：未发货（10 / 20）的货还锁着，要 release；已发货（30 / 40）的货已经扣减，要 replenish。
        // 分不清这两种就会把 locked 减成负数，或者 deducted 减成负数——两边都靠数据库非负约束挡住，
        // 结果是退款「失败」，而实际上钱已经退了。
        var lockedPhase = order.Status is Domain.Entities.OrderStatuses.PendingPayment
            or Domain.Entities.OrderStatuses.PendingShipment;

        // 按**本次退的件数**回补，不是整行数量：部分退款退 1 件就只回补 1 件。
        // 按整行回补的话，买了 3 件退 1 件会把 3 件全部放回库存，直接超卖。
        foreach (var line in resolution.Lines.Where(a => a.OrderItemId != 0 && a.Quantity > 0))
        {
            var item = items.First(a => a.Id == line.OrderItemId);
            try
            {
                if (lockedPhase)
                {
                    await _inventory.ReleaseAsync(
                        item.SkuId, line.Quantity, $"{order.OrderNo}:{item.SkuId}", ct).ConfigureAwait(false);
                }
                else
                {
                    await _inventory.ReplenishAsync(
                        item.SkuId, line.Quantity, $"{order.OrderNo}:{item.SkuId}", ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "退款回补库存失败：订单 {OrderNo} SKU {SkuId} 数量 {Quantity}",
                    order.OrderNo, item.SkuId, line.Quantity);
            }
        }

        _logger.LogInformation(
            "订单 {OrderNo} 退款 {RefundNo} 成功：本次 {Amount} 元，累计 {Total} 元（{Type}），原因 {Reason}",
            order.OrderNo, refundNo, resolution.Total,
            OrderRefundRules.Round2(order.RefundedAmount + resolution.Total),
            OrderRefundTypes.NameOf(refund.RefundType), request.Remark);

        var result = new RefundResultDto(
            refundId, refundNo, resolution.Total,
            OrderRefundRules.Round2(order.RefundedAmount + resolution.Total),
            resolution.RemainingAfter, resolution.FullyRefunded,
            refund.RefundType, OrderRefundTypes.NameOf(refund.RefundType));

        return ApiResults.Ok(
            result,
            resolution.FullyRefunded
                ? $"已整单退款 {resolution.Total:0.00} 元"
                : $"已退款 {resolution.Total:0.00} 元，还剩 {resolution.RemainingAfter:0.00} 元可退");
    }

    /// <summary>生成退款单号：时间戳 + 6 位随机数。</summary>
    /// <returns>退款单号。</returns>
    private static string BuildRefundNo()
        => $"RFD{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.Next(100000, 1000000)}";
}
