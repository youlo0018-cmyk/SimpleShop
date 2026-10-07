using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
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
        // 防 IDOR：客户令牌存在时以令牌里的客户为准，与请求体不一致直接 403
        // （静默改用令牌客户会把客户端的 bug 藏起来，见 CustomerScope 的说明）。
        // 抛出的 BaseApiException 由全局异常中间件转成统一响应，与订单 / 积分 / 购物车同一口径。
        var customerId = CustomerScope.Require(request.CustomerId);

        var result = await _coupons.ClaimAsync(
            customerId, request.ActivityId, request.Quantity, DateTime.UtcNow, ct);

        if (!result.Outcome.Succeeded)
        {
            return ApiResults.Fail<ClaimCouponResult>(BaseApiResponseCode.BusinessError, result.Outcome.Error);
        }

        var msg = result.CouponCodes.Count == 0 ? "您当前没有可领取的券" : $"领取成功，共 {result.CouponCodes.Count} 张";
        return ApiResults.Ok(new ClaimCouponResult(result.CouponCodes, msg), msg);
    }
}

/// <summary>查询当前可领取的券活动。</summary>
public sealed class QueryAvailableCouponsHandler
    : IRequestHandler<QueryAvailableCouponsCommand, ApiResponse<List<CouponActivityItem>>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public QueryAvailableCouponsHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当前可领取的活动列表。</returns>
    public async Task<ApiResponse<List<CouponActivityItem>>> Handle(
        QueryAvailableCouponsCommand request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var (items, _) = await _coupons.PageActivitiesAsync(
            1, 200, string.Empty, 1, 0, ct).ConfigureAwait(false);

        var available = items
            .Where(a => a.ClaimStartTime <= now &&
                        a.ClaimEndTime >= now &&
                        a.ClaimedQuantity < a.ClaimQuantity)
            .ToList();

        var templateIds = available.Select(a => a.TemplateId).Distinct().ToArray();
        var names = templateIds.Length == 0
            ? new Dictionary<long, string>()
            : (await _coupons.ListTemplatesByIdsAsync(templateIds, ct).ConfigureAwait(false))
                .ToDictionary(a => a.Id, a => a.TemplateName);

        // 模板已被删的活动**不再展示**：它必然领不到（ClaimAsync 会以「券模板不存在或已停用」拒绝），
        // 摆在领券中心只是给用户一个点了就报错的按钮。删除入口已经拦住了新数据，
        // 这里兜的是历史遗留的悬空引用。
        available = available.Where(a => names.ContainsKey(a.TemplateId)).ToList();

        var result = available.Select(a => new CouponActivityItem(
            a.Id,
            a.ActivityName,
            a.TemplateId,
            names[a.TemplateId],
            // 带 Z：小程序端按 UTC 解析后再转本地展示，少了标记会整体偏移一个时区
            CouponTimeNormalizer.ToUtcIso(a.ClaimStartTime),
            CouponTimeNormalizer.ToUtcIso(a.ClaimEndTime),
            a.ClaimQuantity,
            a.ClaimedQuantity,
            a.PerUserLimit,
            a.TargetType,
            a.TargetType switch
            {
                1 => "全场",
                2 => "指定商品",
                3 => "指定规格",
                _ => "未知"
            },
            a.SortOrder,
            a.Status,
            a.Status == 1 ? "启用" : "停用",
            a.PlatformId,
            // 领券中心只列模板仍在的活动（上面已按 names 过滤），这里恒为 true。
            true)).ToList();

        return ApiResults.Ok(result);
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
        // 防 IDOR：`/coupons/Occupy` 经网关对客户令牌开放，不校验的话
        // 客户 A 能拿客户 B 的券去占用（把 B 的券锁死）。内部调用（订单服务）没有客户上下文，
        // Require 直接返回请求里的客户 Id，不受影响。
        var customerId = CustomerScope.Require(request.CustomerId);

        var outcome = await _coupons.OccupyAsync(
            customerId, request.OrderNo, request.CouponId, request.Lines, DateTime.UtcNow, ct);

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
            new CouponOccupyResult(outcome.CouponId, outcome.DiscountAmount, outcome.AlreadyApplied, msg)
            {
                // 逐行分摊原样回给订单侧（券的作用域只有这里知道）
                LineDiscounts = outcome.LineDiscounts
            }, msg);
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
        var customerId = CustomerScope.Require(request.CustomerId);
        var outcome = await _coupons.ConsumeAsync(customerId, request.OrderNo, ct);

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
        var customerId = CustomerScope.Require(request.CustomerId);
        var outcome = await _coupons.ReleaseAsync(customerId, request.OrderNo, ct);

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
            // 游客只算活动价不计券（BUSINESS.md 11.5）：这里返回空列表而不是 401
            return ApiResults.Ok(new SettleCouponResult(false, null, Array.Empty<SettleCouponOption>()));
        }

        // 🔴 防 IDOR：结算页试算会把「客户名下有哪些券、各减多少」原样返回，
        // 不校验归属的话，任何登录客户传别人的 customerId 就能看到别人的券包
        // （实测过：A 传 B 的 Id 拿到了 B 的券与优惠额）。
        var customerId = CustomerScope.Require(request.CustomerId);

        var available = await _coupons.ListAvailableAsync(customerId, DateTime.UtcNow, ct);

        var options = new List<SettleCouponOption>();
        IReadOnlyList<decimal> chosenLineDiscounts = [];
        foreach (var coupon in available)
        {
            var quote = CouponCalculator.Quote(coupon, request.Lines);
            if (!quote.ReachedThreshold || quote.DiscountAmount <= 0m) continue;

            options.Add(new SettleCouponOption(
                coupon.Id, coupon.CouponCode, TypeName(coupon.CouponType),
                quote.DiscountAmount, coupon.ExpireAt.ToString("yyyy-MM-dd HH:mm"), false));

            // 客户端当前选中的那张券：把逐行分摊也算出来一起回。
            // 券有作用域（全场 / 指定 SPU / 指定 SKU），只有这里算得对 ——
            // 结算页按全行比例自己分的话，作用域外的行也会被减掉一笔，
            // 与下单后的逐行优惠对不上。
            if (coupon.Id == request.CouponId)
            {
                chosenLineDiscounts = CouponCalculator.AllocateToCoveredLines(
                    coupon, request.Lines, quote.DiscountAmount);
            }
        }

        var hasBest = CouponCalculator.TryPickBest(available, request.Lines, out var bestQuote);
        if (!hasBest)
        {
            return ApiResults.Ok(new SettleCouponResult(false, null, options)
            {
                ChosenLineDiscounts = chosenLineDiscounts
            });
        }

        var bestOption = new SettleCouponOption(
            bestQuote!.Value.CouponId, bestQuote.Value.CouponCode,
            TypeName(available.First(a => a.Id == bestQuote.Value.CouponId).CouponType),
            bestQuote.Value.DiscountAmount,
            available.First(a => a.Id == bestQuote.Value.CouponId).ExpireAt.ToString("yyyy-MM-dd HH:mm"),
            true);

        return ApiResults.Ok(new SettleCouponResult(true, bestOption, options)
        {
            ChosenLineDiscounts = chosenLineDiscounts
        });
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
