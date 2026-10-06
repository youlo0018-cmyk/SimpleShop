using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using FluentValidation;
using MediatR;
using OrderService.Domain.Ports;
using OrderService.Domain.Services;

namespace OrderService.Application.Features.Orders;

/// <summary>结算试算的一行输入。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Quantity">数量。</param>
public readonly record struct PreviewLineInput(long SpuId, long SkuId, int Quantity);

/// <summary>结算试算（<b>只读，不占用任何资源</b>）。</summary>
/// <param name="CustomerId">客户 Id；游客传 0。</param>
/// <param name="PlatformId">平台 Id，0 表示由租户上下文决定。</param>
/// <param name="MerchantId">商户 Id，0 表示平台自营。</param>
/// <param name="Lines">订单行。</param>
/// <param name="CouponId">客户选中的券 Id，0 表示不用券。</param>
/// <param name="PointsToUse">打算抵扣的积分数，0 表示不用积分。</param>
public record PreviewOrderCommand(
    long CustomerId,
    long PlatformId,
    long MerchantId,
    IReadOnlyList<PreviewLineInput> Lines,
    long CouponId = 0,
    long PointsToUse = 0) : IRequest<ApiResponse<PreviewOrderResult>>;

/// <summary>结算试算结果。</summary>
/// <param name="Lines">逐行金额拆分，含纠正后的权威单价。</param>
/// <param name="GoodsTotal">商品总额。</param>
/// <param name="ActivityDiscount">活动优惠合计。</param>
/// <param name="CouponDiscount">券优惠合计。</param>
/// <param name="Freight">运费（按平台配置算，不采信客户端）。</param>
/// <param name="PointsDeduction">积分抵扣金额。</param>
/// <param name="PointsToUse">本次抵扣的积分数。</param>
/// <param name="PayableAmount">实付。</param>
/// <param name="MaxPointsToUse">本次最多可抵扣的积分数（= 应付商品金额的分钱数）。</param>
/// <param name="CouponOptions">可用券及各自优惠额，供结算页展示与切换。</param>
/// <param name="BestCouponId">最优惠的券 Id；没有可用券时为 0。</param>
public sealed record PreviewOrderResult(
    IReadOnlyList<PreviewOrderLine> Lines,
    decimal GoodsTotal,
    decimal ActivityDiscount,
    decimal CouponDiscount,
    decimal Freight,
    decimal PointsDeduction,
    long PointsToUse,
    decimal PayableAmount,
    long MaxPointsToUse,
    IReadOnlyList<PreviewCouponOption> CouponOptions,
    long BestCouponId);

/// <summary>试算结果的一行。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="ProductName">商品名快照。</param>
/// <param name="SkuSpecText">规格文本快照。</param>
/// <param name="DeliveryType">配送方式（以服务端为准）。</param>
/// <param name="UnitPrice">权威单价，<b>不是客户端报的那个</b>。</param>
/// <param name="Quantity">数量。</param>
/// <param name="OriginalAmount">原行金额。</param>
/// <param name="ActivityDiscount">该行活动优惠。</param>
/// <param name="CouponDiscount">该行分摊到的券优惠。</param>
/// <param name="PayableAmount">该行应付。</param>
public sealed record PreviewOrderLine(
    long SpuId, long SkuId, string ProductName, string SkuSpecText, int DeliveryType,
    decimal UnitPrice, int Quantity, decimal OriginalAmount,
    decimal ActivityDiscount, decimal CouponDiscount, decimal PayableAmount);

/// <summary>一张可用券在当前订单下的试算结果。</summary>
/// <param name="CouponId">用户券 Id。</param>
/// <param name="CouponTypeName">券类型中文名。</param>
/// <param name="DiscountAmount">该券可减金额。</param>
/// <param name="ExpireAt">过期时间。</param>
/// <param name="IsBest">是否最优。</param>
public sealed record PreviewCouponOption(
    long CouponId, string CouponTypeName, decimal DiscountAmount, string ExpireAt, bool IsBest);

/// <summary>结算试算处理器。</summary>
/// <remarks>
/// <para><b>为什么必须有这个接口</b>：小程序结算页此前把金额全部在前端算
/// （商品金额 − 券优惠），既不含运费也不含活动与积分。</para>
/// <para>后果不只是「少显示几行」：前端算出来的「预计应付」与真实下单金额不一致，
/// 用户看到 51、实际被扣 61，而界面上没有任何地方解释这 10 元的差额。
/// 金额必须由服务端出，前端只负责显示。</para>
/// </remarks>
public sealed class PreviewOrderHandler
    : IRequestHandler<PreviewOrderCommand, ApiResponse<PreviewOrderResult>>
{
    private readonly OrderPricingResolver _resolver;
    private readonly IActivityPort _activities;
    private readonly ICouponPort _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="resolver">定价解析器。</param>
    /// <param name="activities">活动优惠试算端口。</param>
    /// <param name="coupons">券端口，只用只读试算。</param>
    public PreviewOrderHandler(
        OrderPricingResolver resolver, IActivityPort activities,
        ICouponPort coupons)
    {
        _resolver = resolver;
        _activities = activities;
        _coupons = coupons;
    }

    /// <summary>执行试算。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>金额拆分与可用券。</returns>
    public async Task<ApiResponse<PreviewOrderResult>> Handle(
        PreviewOrderCommand request, CancellationToken ct)
    {
        // 纠正成服务端认可的行：单价、配送方式、可售状态。
        // 刻意**不读 request 里的单价与运费** —— 试算报出来的必须是真正会收的数，
        // 否则「结算页显示 51、下单扣 61」的问题只是从计算挪到了显示。
        var lines = request.Lines
            .Select(a => new OrderLineRequest(
                a.SpuId, a.SkuId, a.Quantity, 0m, string.Empty, string.Empty, 0))
            .ToArray();

        var outcome = await _resolver.ResolveAsync(lines, ct).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<PreviewOrderResult>(BaseApiResponseCode.BusinessError, outcome.Error);
        }

        var resolved = outcome.Lines;
        var ctx = TenantContextHolder.Current;

        // 与下单同一口径：平台以**商品**为准。
        // 客户令牌里没有 platform_id，小程序硬编码 0，照它算就会漏掉平台运费 ——
        // 试算显示 113.50、下单收 123.50，差额在界面上无处可解释。
        var platformId = outcome.PlatformId > 0
            ? outcome.PlatformId
            : (ctx.PlatformId > 0 ? ctx.PlatformId : request.PlatformId);

        var amountLines = resolved
            .Select(a => new OrderLineInput(a.SkuId, a.Quantity, a.UnitPrice)).ToArray();

        // 可用券：只读，绝不占用。用户每看一眼结算页就锁掉一张券是不能接受的。
        var couponPortLines = resolved
            .Select(a => new CouponPortLine(
                a.SpuId, a.SkuId, OrderAmountCalculator.Round2(a.UnitPrice * a.Quantity)))
            .ToArray();
        var options = await _coupons.QuoteAsync(request.CustomerId, couponPortLines, ct)
            .ConfigureAwait(false);

        // 客户选中的券；没选（0）或选的那张已经不可用时按 0 优惠算，
        // 与下单口径一致 —— 下单时占不到券也是照常下单。
        var chosen = options.FirstOrDefault(a => a.CouponId == request.CouponId);
        var couponDiscount = chosen.CouponId > 0 ? chosen.DiscountAmount : 0m;

        var activityBySku = await _activities.QuoteAsync(
            request.CustomerId, platformId, 0, request.CouponId,
            couponPortLines.Select(a => (a.SpuId, a.SkuId, a.Amount)).ToArray(),
            ct).ConfigureAwait(false);

        var activityDiscounts = couponPortLines
            .Select(a => activityBySku.FirstOrDefault(b => b.SkuId == a.SkuId).ActivityDiscount)
            .ToArray();

        var freight = await _resolver.ResolveFreightAsync(platformId, resolved, ct).ConfigureAwait(false);

        // 先不算积分算一遍，拿到商品实付 —— 它决定最多能抵多少积分。
        var beforePoints = OrderAmountCalculator.Calculate(
            amountLines,
            OrderAmountCalculator.AllocateCouponDiscount(amountLines, couponDiscount),
            activityDiscounts,
            freight,
            pointsToUse: 0);

        // 积分只能抵扣**商品金额**（1 分 = 1 分钱），不抵运费（BUSINESS.md 8.2）。
        // 刻意**不**在这里查积分余额：余额前端自己已经拿到了，
        // 为此多打一次跨服务调用并不划算；真正的余额校验在下单锁定积分那一步做，
        // 试算阶段把「抵扣额不能超过商品金额」这个上限算清楚就够了。
        //
        // 不加这个上限的话，用户输 10000 分会让实付直接变成 0，
        // 而界面上「可用 500 积分」与「抵扣 10000 积分」摆在一起，非常费解。
        // ⚠️ 单位：GoodsTotal 是**元**，积分是**分**，必须 ×100。
        // 与 OrderCreator 的同一处算法保持一致，两边少一次乘就出现「试算能抵 1 元、下单只认 1 分」。
        var maxPoints = (long)Math.Floor(beforePoints.GoodsTotal * 100m);
        var pointsToUse = Math.Clamp(request.PointsToUse, 0L, maxPoints);

        var amount = OrderAmountCalculator.Calculate(
            amountLines,
            OrderAmountCalculator.AllocateCouponDiscount(amountLines, couponDiscount),
            activityDiscounts,
            freight,
            pointsToUse);

        var result = new PreviewOrderResult(
            resolved.Select((line, i) => new PreviewOrderLine(
                line.SpuId, line.SkuId, line.ProductName, line.SkuSpecText, line.DeliveryType,
                line.UnitPrice, line.Quantity,
                amount.Lines[i].OriginalAmount, amount.Lines[i].ActivityDiscount,
                amount.Lines[i].CouponDiscount, amount.Lines[i].PayableAmount)).ToList(),
            amount.GoodsTotal,
            OrderAmountCalculator.Round2(amount.Lines.Sum(a => a.ActivityDiscount)),
            couponDiscount,
            amount.Freight,
            amount.PointsDeduction,
            pointsToUse,
            amount.PayableAmount,
            maxPoints,
            options.Select(a => new PreviewCouponOption(
                a.CouponId, a.CouponTypeName, a.DiscountAmount, a.ExpireAt, a.IsBest)).ToList(),
            options.FirstOrDefault(a => a.IsBest).CouponId);

        return ApiResults.Ok(result);
    }
}

/// <summary>结算试算命令校验器。</summary>
public sealed class PreviewOrderValidator : AbstractValidator<PreviewOrderCommand>
{
    /// <summary>构造校验器。</summary>
    public PreviewOrderValidator()
    {
        RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户 Id 不正确");
        RuleFor(x => x.PlatformId).GreaterThanOrEqualTo(0).WithMessage("平台 Id 不正确");
        RuleFor(x => x.MerchantId).GreaterThanOrEqualTo(0).WithMessage("商户 Id 不正确");
        RuleFor(x => x.CouponId).GreaterThanOrEqualTo(0).WithMessage("券 Id 不正确");
        RuleFor(x => x.PointsToUse).GreaterThanOrEqualTo(0).WithMessage("抵扣积分不能为负数");
        RuleFor(x => x.Lines).NotEmpty().WithMessage("订单行不能为空");

        // 行数上限挡住「拿一个上万行的试算请求把服务打满」——
        // 试算是纯查询但会连打商品、营销、券三个服务，成本不比下单低。
        RuleFor(x => x.Lines.Count).LessThanOrEqualTo(50).WithMessage("订单行数超出上限");

        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(a => a.SpuId).GreaterThan(0).WithMessage("商品 Id 不正确");
            line.RuleFor(a => a.SkuId).GreaterThan(0).WithMessage("规格 Id 不正确");
            line.RuleFor(a => a.Quantity).GreaterThan(0).WithMessage("购买数量必须大于 0");
        });
    }
}

