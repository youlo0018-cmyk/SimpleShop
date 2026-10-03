using Collaboration.Domain.Common;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;
using MarketingService.Domain.Services;
using MediatR;

namespace MarketingService.Application.Features.Coupon;

/// <summary>领券处理器。</summary>
public sealed class ClaimCouponHandler : IRequestHandler<ClaimCouponCommand, ApiResponse<ClaimCouponResult>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public ClaimCouponHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <summary>执行领券。</summary>
    /// <param name="request">领券命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回券码列表。</returns>
    public async Task<ApiResponse<ClaimCouponResult>> Handle(ClaimCouponCommand request, CancellationToken ct)
    {
        var result = await _coupons.ClaimAsync(
            request.CustomerId, request.ActivityId, request.Quantity, DateTime.UtcNow, ct);

        if (!result.Outcome.Succeeded)
        {
            return ApiResults.Fail<ClaimCouponResult>(BaseApiResponseCode.BusinessError, result.Outcome.Error);
        }

        var msg = result.CouponCodes.Count == 0 ? "您当前没有可领取的券" : $"领取成功，共 {result.CouponCodes.Count} 张";
        return ApiResults.Ok(new ClaimCouponResult(result.CouponCodes, msg), msg);
    }
}

/// <summary>占券处理器。</summary>
public sealed class OccupyCouponHandler : IRequestHandler<OccupyCouponCommand, ApiResponse<CouponOccupyResult>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public OccupyCouponHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <summary>执行占券。</summary>
    /// <param name="request">占券命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回实际优惠金额。</returns>
    public async Task<ApiResponse<CouponOccupyResult>> Handle(OccupyCouponCommand request, CancellationToken ct)
    {
        var outcome = await _coupons.OccupyAsync(
            request.CustomerId, request.OrderNo, request.CouponId, request.Lines, DateTime.UtcNow, ct);

        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<CouponOccupyResult>(BaseApiResponseCode.BusinessError, outcome.Error);
        }

        // 没占到券不算失败：客户可能没券、券都够不着门槛，此时就是不优惠而已
        var msg = outcome.CouponId <= 0
            ? "无可用券，本单不使用券"
            : outcome.AlreadyApplied
                ? "该订单已占券，返回首次结果"
                : $"占券成功，优惠 {outcome.DiscountAmount:0.00}";

        return ApiResults.Ok(
            new CouponOccupyResult(outcome.CouponId, outcome.DiscountAmount, outcome.AlreadyApplied, msg), msg);
    }
}

/// <summary>核销券处理器。</summary>
public sealed class ConsumeCouponHandler : IRequestHandler<ConsumeCouponCommand, ApiResponse<CouponOccupyResult>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public ConsumeCouponHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <summary>执行核销。</summary>
    /// <param name="request">核销命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse<CouponOccupyResult>> Handle(ConsumeCouponCommand request, CancellationToken ct)
    {
        var outcome = await _coupons.ConsumeAsync(request.CustomerId, request.OrderNo, ct);

        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<CouponOccupyResult>(BaseApiResponseCode.BusinessError, outcome.Error);
        }

        var msg = outcome.CouponId <= 0 ? "该订单未占券，无需核销" : "核销成功";
        return ApiResults.Ok(new CouponOccupyResult(outcome.CouponId, outcome.DiscountAmount, outcome.AlreadyApplied, msg), msg);
    }
}

/// <summary>回退占券处理器。</summary>
public sealed class ReleaseCouponHandler : IRequestHandler<ReleaseCouponCommand, ApiResponse<CouponOccupyResult>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public ReleaseCouponHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <summary>执行回退。</summary>
    /// <param name="request">回退命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse<CouponOccupyResult>> Handle(ReleaseCouponCommand request, CancellationToken ct)
    {
        var outcome = await _coupons.ReleaseAsync(request.CustomerId, request.OrderNo, ct);

        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<CouponOccupyResult>(BaseApiResponseCode.BusinessError, outcome.Error);
        }

        var msg = outcome.CouponId <= 0 ? "该订单未占券，无需回退" : "回退成功，券已回到可用";
        return ApiResults.Ok(new CouponOccupyResult(outcome.CouponId, 0m, outcome.AlreadyApplied, msg), msg);
    }
}

/// <summary>结算试算处理器。</summary>
public sealed class SettleCouponsHandler : IRequestHandler<SettleCouponsCommand, ApiResponse<SettleCouponResult>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public SettleCouponsHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <summary>执行试算。</summary>
    /// <param name="request">试算命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可用券列表与最优券。</returns>
    /// <remarks>
    /// 游客（CustomerId = 0）**只算活动价不计券**——游客没有券包，也领不了券（BUSINESS.md 13）。
    /// 这里直接返回无可用券，而不是去查一个 customer_id=0 的券包。
    /// </remarks>
    public async Task<ApiResponse<SettleCouponResult>> Handle(SettleCouponsCommand request, CancellationToken ct)
    {
        if (request.CustomerId <= 0)
        {
            return ApiResults.Ok(new SettleCouponResult(false, null, Array.Empty<SettleCouponOption>()));
        }

        var available = await _coupons.ListAvailableAsync(request.CustomerId, DateTime.UtcNow, ct);

        var options = new List<SettleCouponOption>();
        foreach (var coupon in available)
        {
            var quote = CouponCalculator.Quote(coupon, request.Lines);
            if (!quote.ReachedThreshold || quote.DiscountAmount <= 0m) continue;

            options.Add(new SettleCouponOption(
                coupon.Id, coupon.CouponCode, TypeName(coupon.CouponType),
                quote.DiscountAmount, coupon.ExpireAt.ToString("yyyy-MM-dd HH:mm"), false));
        }

        var hasBest = CouponCalculator.TryPickBest(available, request.Lines, out var bestQuote);
        if (!hasBest)
        {
            return ApiResults.Ok(new SettleCouponResult(false, null, options));
        }

        var bestOption = new SettleCouponOption(
            bestQuote!.Value.CouponId, bestQuote.Value.CouponCode,
            TypeName(available.First(a => a.Id == bestQuote.Value.CouponId).CouponType),
            bestQuote.Value.DiscountAmount,
            available.First(a => a.Id == bestQuote.Value.CouponId).ExpireAt.ToString("yyyy-MM-dd HH:mm"),
            true);

        return ApiResults.Ok(new SettleCouponResult(true, bestOption, options));
    }

    private static string TypeName(int type) => type switch
    {
        CouponTypes.FullReduction => "满减券",
        CouponTypes.Discount => "折扣券",
        CouponTypes.Cash => "代金券",
        CouponTypes.Gift => "满赠券",
        _ => "未知"
    };
}