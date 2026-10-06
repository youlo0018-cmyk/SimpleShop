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
    private readonly IPlatformPort _platforms;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="platforms">平台端口：列表要显示平台 / 店铺名而不是 Id（DATA_SPEC 4.3）。</param>
    public QueryAdminOrdersHandler(IOrderStore store, IPlatformPort platforms)
    {
        _store = store;
        _platforms = platforms;
    }

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

        // 平台名 / 店铺名只存在于商户平台服务：一次批量取，取不到回落成 Id
        var names = await _platforms.GetNamesAsync(
            orders.Where(a => a.PlatformId > 0).Select(a => a.PlatformId).Distinct().ToArray(),
            orders.Where(a => a.MerchantId > 0).Select(a => a.MerchantId).Distinct().ToArray(),
            ct).ConfigureAwait(false);

        var items = orders.Select(a =>
        {
            aggregates.TryGetValue(a.Id, out var agg);
            return new AdminOrderListItemDto(
                a.Id, a.OrderNo, a.CustomerId, a.Status, OrderStatusMachine.NameOf(a.Status),
                a.PayableAmount, agg.ItemQuantity,
                a.ReceiverName, a.ReceiverPhone,
                a.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                agg.HasPhysical, agg.HasVirtual, agg.HasSelfPickup,
                a.CustomerNo, a.MerchantId,
                a.PlatformId > 0
                    ? names.Platforms.GetValueOrDefault(a.PlatformId, a.PlatformId.ToString())
                    : "平台自营",
                a.MerchantId > 0
                    ? names.Merchants.GetValueOrDefault(a.MerchantId, a.MerchantId.ToString())
                    : "平台自营");
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

        // 行级「可退金额」必须**受订单剩余余额约束**，不能直接用「行实付 − 行已退」。
        // 订单用了券 / 积分时，订单实付 < 行实付之和，于是行级可退会比订单还能退的多；
        // 后台退款表单照着行级数字填，提交必被「超过订单剩余可退」挡回来，
        // 而界面上完全看不出该填多少。
        // refunded 是 orderItemId → 余额 的字典，先转成按订单行分组的字典再算，
        // 否则每行都要全表扫一遍
        var refundedByItem = refunded.Values
            .GroupBy(r => r.OrderItemId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        var lineBalances = items
            .Select(a => Math.Max(0m, a.PayableAmount
                - (refundedByItem.TryGetValue(a.Id, out var done) ? done : 0m)))
            .ToArray();
        var lineBalanceTotal = Math.Round(lineBalances.Sum(), 2, MidpointRounding.AwayFromZero);
        var orderRemaining = Math.Max(0m, Math.Round(
            order.PayableAmount - order.RefundedAmount, 2, MidpointRounding.AwayFromZero));

        // 按行占比摊订单余额；订单余额大于行余额之和时（运费）不摊，
        // 差额仍留在行上，由整单退时的运费分摊行体现。
        var scale = lineBalanceTotal > orderRemaining && lineBalanceTotal > 0m
            ? orderRemaining / lineBalanceTotal
            : 1m;

        var itemDtos = items.Select((a, idx) =>
        {
            refunded.TryGetValue(a.Id, out var r);
            return new OrderItemDto(
                a.Id, a.SkuId, a.SpuId, a.ProductName, a.SkuSpecText,
                a.Price, a.Quantity, a.OriginalAmount,
                a.ActivityDiscount, a.CouponDiscount, a.PayableAmount, a.DeliveryType,
                a.SourceType,
                r.Quantity,
                Math.Max(0m, decimal.Round(
                    lineBalances[idx] * scale, 2, MidpointRounding.AwayFromZero)));
        }).ToList();

        return ApiResults.Ok(
            OrderDetailAssembler.Build(order, items) with { Items = itemDtos });
    }
}

/// <summary>订单退款记录查询处理器。</summary>
/// <remarks>
/// 返回的是**订单侧**的退款记录（后台代客退款，含部分退款）。
/// 支付服务那边还有一套「客户申请 → 审批」的退款单，那是两条不同的链路，
/// 前端订单详情要把两者分区展示，不要混成一张表。
/// </remarks>
public sealed class QueryOrderRefundsHandler
    : MediatR.IRequestHandler<QueryOrderRefundsCommand, ApiResponse<IReadOnlyList<OrderRefundDto>>>
{
    private readonly IOrderStore _store;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    public QueryOrderRefundsHandler(IOrderStore store) => _store = store;

    /// <summary>查询退款记录。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>退款记录列表；没有退款时是空列表而不是 null。</returns>
    public async Task<ApiResponse<IReadOnlyList<OrderRefundDto>>> Handle(
        QueryOrderRefundsCommand request, CancellationToken ct)
    {
        var rows = await _store.ListRefundsAsync(request.OrderId, ct).ConfigureAwait(false);

        var list = rows.Select(pair => new OrderRefundDto(
            pair.Refund.Id,
            pair.Refund.RefundNo,
            pair.Refund.Amount,
            pair.Refund.RefundType,
            OrderRefundTypes.NameOf(pair.Refund.RefundType),
            pair.Refund.FullyRefunded,
            pair.Refund.Reason,
            pair.Refund.OperatorName,
            pair.Refund.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            pair.Items.Select(a => new OrderRefundItemDto(
                a.OrderItemId, a.ProductName, a.SkuSpecText, a.Quantity, a.Amount)).ToList()))
            .ToList();

        return ApiResults.Ok<IReadOnlyList<OrderRefundDto>>(list);
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
    private readonly ILogger<ShipOrderHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="logistics">物流公司查询端口。</param>
    /// <param name="logger">日志器。</param>
    public ShipOrderHandler(
        IOrderStore store, ILogisticsCompanyPort logistics, ILogger<ShipOrderHandler> logger)
    {
        _store = store;
        _logistics = logistics;
        _logger = logger;
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

        // 这个入口只服务**快递**行：虚拟单走「虚拟发货」（20 → 30，不填物流信息），
        // 自提单走「备货完成」（20 → 40，生成取货码）。
        // 走错入口的后果不是报个错而已：自提单被「发出去」会变成 30 待收货，
        // 顾客既没有取货码也没有包裹可收；虚拟单被填上运单号，
        // 客服与顾客都会以为有包裹可查。
        var items = await _store.ListItemsAsync(order.Id, ct).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.InternalError, "订单没有商品行，无法判断配送方式");
        }

        var hasExpress = items.Any(a => a.DeliveryType == Domain.Entities.DeliveryTypes.Express);

        if (!hasExpress)
        {
            var isSelfPickupOnly = items.All(a => a.DeliveryType == Domain.Entities.DeliveryTypes.SelfPickup);
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BusinessError,
                isSelfPickupOnly
                    ? "该订单是自提商品，请用「备货完成」生成取货码，不要走发货"
                    : "该订单是虚拟商品，请用「虚拟发货」（不需要物流信息）");
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

        _logger.LogInformation("订单 {OrderNo} 已发货：{Company} {TrackingNo}", order.OrderNo, companyName, trackingNo);
        return ApiResponseFactory.Ok($"已发货 · {companyName} {trackingNo}");
    }
}

/// <summary>虚拟商品发货处理器（20 → 30 待收货，不填物流信息）。</summary>
/// <remarks>
/// <para>规格 BUSINESS.md 7.1 的状态表写得很明确：30 待收货适用于「快递 / 虚拟」，
/// 进入条件是「商户发货（快递必填物流信息；虚拟不填）」；50 已完成的进入条件是
/// 「快递/虚拟：用户确认收货；自提：商户核销取货码」。所以虚拟单和快递单走的是
/// 同一条状态链，区别只在物流字段。</para>
///
/// <para>用户的需求确认表也写着「虚拟退款：确认收货后不可退款」——
/// 没有「确认收货」这一步的话，这条规则对虚拟订单根本没有落点。</para>
///
/// <para>曾经这里是 20 → 50「发货即完成」：那是把「不填物流信息」误读成了「交付即完成」。
/// 后果是虚拟单永远进不了 30，而虚拟商品的退款窗口是 {20,30}（规格 10.2）——
/// 窗口的一半成了死代码，「确认收货后不可退」也就无从谈起。</para>
/// </remarks>
public sealed class DeliverVirtualHandler : MediatR.IRequestHandler<DeliverVirtualCommand, ApiResponse>
{
    private readonly IOrderStore _store;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    public DeliverVirtualHandler(IOrderStore store) => _store = store;

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

        // 幂等：这单确实是虚拟单，且已经在待收货 / 已完成（说明之前发过），
        // 就回「已发货」而不是报错 —— 运营在列表上误点两下是常事。
        if (order.Status is Domain.Entities.OrderStatuses.PendingReceipt
            or Domain.Entities.OrderStatuses.Completed)
        {
            return ApiResponseFactory.Ok("该订单已发货");
        }

        var affected = await _store.TryTransitStatusAsync(
            order.Id,
            Domain.Entities.OrderStatuses.PendingShipment,
            Domain.Entities.OrderStatuses.PendingReceipt, ct: ct).ConfigureAwait(false);

        if (affected == 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.OrderStateInvalid, "订单状态已变更，请刷新后重试");
        }

        // 积分不在这里发：虚拟单也要等顾客**确认收货**（30 → 50）才算完成，
        // 发积分的那条路在 ConfirmReceipt 里（三条进「已完成」的路各自负责自己的奖励）。
        return ApiResponseFactory.Ok("已发货（虚拟商品，无需物流信息），等待顾客确认收货");
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
        //
        // 🔴 判据必须是**令牌里的租户**，不是请求体里的 PlatformId / MerchantId。
        // 旧实现读请求体，而这两个字段默认都是 0，旧判据又把 `<= 0` 当成「不限」——
        // 于是商户只要**不传**这两个字段，这道校验就整个失效。
        // 配合「订单表此前没有租户过滤」这一条，商户 A 能把商户 B 的自提单核销掉。
        var tenant = TenantContextHolder.Current;
        if (!BelongsToTenant(order, tenant))
        {
            _logger.LogWarning("商户 {MerchantId} 试图核销不属于自己的取货码，订单 {OrderNo}",
                tenant.MerchantId, order.OrderNo);
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

    /// <summary>订单是否属于当前令牌的租户。</summary>
    /// <param name="order">订单。</param>
    /// <param name="ctx">当前租户上下文（来自令牌，不是请求体）。</param>
    /// <returns>属于返回 true。</returns>
    /// <remarks>
    /// <para><b>只有超管是不受限的</b>。旧签名把「platformId / merchantId &lt;= 0」当成不限，
    /// 而调用方传的是请求体里的字段、默认值正是 0 —— 等于默认放行。</para>
    ///
    /// <para>平台账号看本平台，商户账号看本商户；客户 / 游客 / 内部调用一律不属于后台核销范围。</para>
    /// </remarks>
    private static bool BelongsToTenant(Order order, TenantContext ctx)
    {
        if (ctx.IsSuperAdmin) return true;
        if (ctx.IsMerchant) return order.MerchantId == ctx.MerchantId && order.PlatformId == ctx.PlatformId;
        if (ctx.IsPlatform) return order.PlatformId == ctx.PlatformId;
        return false;
    }
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
    private readonly IPointPort _points;
    private readonly ISeckillPort _seckill;
    private readonly ILogger<RefundOrderHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="inventory">库存端口。</param>
    /// <param name="points">积分端口（退款按比例回收已扣积分）。</param>
    /// <param name="seckill">秒杀端口（秒杀单退款要把货还回秒杀池）。</param>
    /// <param name="logger">日志器。</param>
    public RefundOrderHandler(
        IOrderStore store,
        IInventoryPort inventory,
        IPointPort points,
        ISeckillPort seckill,
        ILogger<RefundOrderHandler> logger)
    {
        _store = store;
        _inventory = inventory;
        _points = points;
        _seckill = seckill;
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

        // 退款窗口统一交给 OrderStatusMachine.CanRefund 判定（BUSINESS 10.2）。
        // 这里原来单独写了一条「虚拟商品订单不支持退款」——比规格严得多，
        // 而且和 CanRefund 的判定重复：两处各写一遍，改了一处另一处就漂了。
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

        // 回补库存：未支付（10）的货还占着 locked，要 release；
        // 已支付（20 / 30 / 40）的货早就从 locked 扣成 deducted 了，要 replenish。
        // 用错会把某一个计数减成负数 —— 而负数会被下游按「不足」拒掉，
        // 于是钱退了、货没回库，只在日志里留一行 error，页面上一片正常。
        // 🔴 只有**未支付**（10 待支付）的单还占着 locked。
        // 一旦支付成功，库存就从 locked 变成 deducted 了（BUSINESS 9.2）：
        // 10 → release（locked → available），20/30/40 → replenish（deducted → available）。
        //
        // 之前把 10 和 20 一起当成「还锁着」，于是**已付款未发货**的订单退款时
        // 去 release 一笔已经 deducted 的量 —— 那个计数直接不够，调用报错被 catch 吞掉，
        // 结果是钱退了、货没回库，页面上只留下一行 error 日志。
        var lockedPhase = order.Status == Domain.Entities.OrderStatuses.PendingPayment;

        // 按**本次退的件数**回补，不是整行数量：部分退款退 1 件就只回补 1 件。
        // 按整行回补的话，买了 3 件退 1 件会把 3 件全部放回库存，直接超卖。
        foreach (var line in resolution.Lines.Where(a => a.OrderItemId != 0 && a.Quantity > 0))
        {
            var item = items.First(a => a.Id == line.OrderItemId);
            try
            {
                // 🔴 秒杀单**从没锁过常规库存**（货在发布场次时就划走了），
                // 所以未支付时走常规 release 必然失败 —— 那笔锁定根本不存在。
                // 正确做法是把 sold_count 减回去，货由场次结束的
                // 「seckill_stock − sold_count」自然回到常规池。
                // 不这么处理的话，这一件会永久滞留在账外：常规池没有、秒杀池也没有。
                var isSeckill = item.SourceType == OrderSourceTypes.Seckill;

                // 🔴 秒杀行**不看 lockedPhase**：它的货在发布场次时就划走了，
                // 与常规池的 locked / deducted 两个计数**都没有关系**。
                //
                // 之前这里是 `isSeckill && lockedPhase`，于是**已支付**的秒杀单
                // （状态 20，lockedPhase 为 false）会掉进下面的常规分支去 replenish ——
                // 把一件从未从常规池拿走的货加回常规池（凭空多货），
                // 同时 sold_count 不回落（那件货永远滞留在账外）。
                // 两条账同时错，而页面上只显示「已退款」。
                if (isSeckill)
                {
                    await _seckill.ReleaseGrabAsync(
                        order.CustomerId, item.SkuId, line.Quantity, order.OrderNo, ct)
                        .ConfigureAwait(false);
                }
                else if (lockedPhase)
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

        // 按比例回收该单已扣的积分（BUSINESS.md 10.3：整单退全退、部分退按比例向上取整、
        // 退回原冻结批次不重算有效期）。
        //
        // 🔴 比例必须用「**本次**退款额 ÷ 实付」而不是「退完没退完」：
        // 部分退款退两次时，两次的比例要能累加回 1，否则客户会被重复回收或回收不足。
        if (order.PointsUsed > 0 && order.PayableAmount > 0)
        {
            var ratio = Math.Clamp(resolution.Total / order.PayableAmount, 0m, 1m);
            try
            {
                await _points.RecoverByRefundAsync(
                    order.CustomerId, order.OrderNo, ratio, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 回收失败**不反过来让退款失败**：钱已经退给客户了，
                // 这时抛错会让前端以为没退、于是重试，直接变成二次退款。
                // 正确做法是记 Error 事后对账 —— 积分与库存用的是同一套取舍。
                _logger.LogError(ex,
                    "退款已生效但积分回收失败：订单 {OrderNo} 比例 {Ratio}，需人工补回收",
                    order.OrderNo, ratio);
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
