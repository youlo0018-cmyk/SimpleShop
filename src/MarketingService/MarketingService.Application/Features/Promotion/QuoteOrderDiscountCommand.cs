using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;
using MarketingService.Domain.Services;

namespace MarketingService.Application.Features.Promotion;

/// <summary>订单行（用于内部试算）。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Amount">该行金额（原价 × 数量），两位小数。</param>
public sealed record QuoteOrderLine(long SpuId, long SkuId, decimal Amount);

/// <summary>下单时按行试算**活动优惠**（订单服务调用）。</summary>
/// <param name="CustomerId">客户 Id，0 表示游客。</param>
/// <param name="Lines">订单行。</param>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
/// <param name="SessionId">秒杀场次 Id，0 表示非秒杀单。</param>
/// <param name="CouponId">客户已选的券 Id，0 表示不用券。</param>
/// <remarks>
/// <para>为什么必须有这个内部接口：结算试算（FinalPrice）会把活动优惠算给前端看，
/// 但<b>下单链路算不到它</b> —— 小程序只传 couponId / pointsToUse / freight，
/// 订单服务于是把 activityDiscount 写成全 0。
/// 结果是结算页显示 41、点下单实收 51，优惠凭空消失。</para>
/// <para>刻意<b>原样复用 <see cref="PromotionCalculator"/> 与同一份优先级配置</b>：
/// 下单金额必须与结算报价逐分一致，两边各算一次必然会漂移。
/// 传 CouponId 是为了走通「活动与券互斥」——只算活动不传券，
/// 会把本该被券覆盖的行也算一遍活动，等于优惠两次。</para>
/// </remarks>
public record QuoteOrderDiscountCommand(
    long CustomerId, IReadOnlyList<QuoteOrderLine> Lines,
    long PlatformId = 0, long SessionId = 0, long CouponId = 0)
    : IRequest<ApiResponse<QuoteOrderDiscountResult>>;

/// <summary>逐行优惠拆分。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="ActivityDiscount">该行活动优惠额。</param>
public sealed record QuoteOrderLineResult(long SpuId, long SkuId, decimal ActivityDiscount);

/// <summary>下单优惠试算结果。</summary>
/// <param name="Lines">逐行拆分。</param>
/// <param name="ActivityDiscountTotal">活动优惠合计。</param>
public sealed record QuoteOrderDiscountResult(
    IReadOnlyList<QuoteOrderLineResult> Lines, decimal ActivityDiscountTotal);

/// <summary>下单优惠试算处理器。</summary>
public sealed class QuoteOrderDiscountHandler
    : IRequestHandler<QuoteOrderDiscountCommand, ApiResponse<QuoteOrderDiscountResult>>
{
    private readonly IPromotionRepository _promotions;
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="promotions">活动仓储。</param>
    /// <param name="coupons">券仓储。</param>
    public QuoteOrderDiscountHandler(IPromotionRepository promotions, ICouponRepository coupons)
    {
        _promotions = promotions;
        _coupons = coupons;
    }

    /// <summary>执行试算。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>逐行活动优惠。</returns>
    public async Task<ApiResponse<QuoteOrderDiscountResult>> Handle(
        QuoteOrderDiscountCommand request, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var lines = request.Lines
            .Select(a => new PromotionLine(a.SpuId, a.SkuId, PromotionCalculator.Round2(a.Amount)))
            .ToArray();

        var activities = await _promotions.ListActiveAsync(
            request.PlatformId, request.SessionId, nowUtc, ct).ConfigureAwait(false);
        var priority = await _promotions.GetPriorityAsync(request.PlatformId, ct).ConfigureAwait(false);

        // 只把**客户已选的那一张券**放进去，其它可用券一律不给：
        // 下单金额必须按「他实际选中的券」算，而不是「系统替他挑的券」。
        var coupons = new List<UserCoupon>();
        if (request.CouponId > 0)
        {
            var owned = await _coupons.ListAvailableAsync(
                request.CustomerId, nowUtc, ct).ConfigureAwait(false);
            coupons = owned.Where(a => a.Id == request.CouponId).ToList();
        }

        var result = PromotionCalculator.Calculate(lines, activities, coupons, priority, nowUtc);

        var perLine = result.Lines
            .Select(a => new QuoteOrderLineResult(
                a.SpuId, a.SkuId, PromotionCalculator.Round2(a.ActivityDiscount)))
            .ToList();

        return ApiResults.Ok(new QuoteOrderDiscountResult(
            perLine, PromotionCalculator.Round2(result.ActivityDiscountTotal)));
    }
}

/// <summary>下单优惠试算校验。</summary>
public sealed class QuoteOrderDiscountValidator : AbstractValidator<QuoteOrderDiscountCommand>
{
    /// <summary>构造校验器。</summary>
    public QuoteOrderDiscountValidator()
    {
        RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户 Id 不正确");
        RuleFor(x => x.Lines).NotEmpty().WithMessage("订单行不能为空");
        // 行数上限挡住「拿一个上万行的请求把服务打满」
        RuleFor(x => x.Lines.Count).LessThanOrEqualTo(50).WithMessage("订单行数超出上限");
        RuleFor(x => x.PlatformId).GreaterThanOrEqualTo(0).WithMessage("平台 Id 不正确");
        RuleFor(x => x.CouponId).GreaterThanOrEqualTo(0).WithMessage("券 Id 不正确");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(a => a.SpuId).GreaterThan(0).WithMessage("商品 Id 不正确");
            line.RuleFor(a => a.SkuId).GreaterThan(0).WithMessage("规格 Id 不正确");
            line.RuleFor(a => a.Amount).GreaterThan(0).WithMessage("行金额必须大于 0");
        });
    }
}
