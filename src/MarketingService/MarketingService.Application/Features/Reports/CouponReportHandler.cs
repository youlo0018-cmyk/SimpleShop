using Collaboration.Domain.Common;
using Collaboration.Domain.Services;
using MarketingService.Domain.IRepository;
using MediatR;

namespace MarketingService.Application.Features.Reports;

/// <summary>营销效果报表——券部分（BUSINESS.md 17）。</summary>
/// <param name="Range">时间范围档位：1 今日 / 2 昨日 / 3 近 7 天 / 4 近 30 天。</param>
/// <param name="RangeName">时间范围中文名。</param>
/// <param name="From">区间起（含）。</param>
/// <param name="To">区间止（不含）。</param>
/// <param name="IssuedTotal">发放总数：运营配的库存总量。</param>
/// <param name="ReceivedTotal">领取数：区间内客户实际领走。</param>
/// <param name="ConsumedTotal">核销数：区间内真正用掉。</param>
/// <param name="ConsumeRate">核销率 = 核销数 / 领取数，以 0~1 小数下发。</param>
/// <param name="DiscountTotal">折扣总额：核销掉的券实际让利合计（两位小数）。</param>
public sealed record CouponReport(
    int Range, string RangeName, DateTime From, DateTime To,
    long IssuedTotal, long ReceivedTotal, long ConsumedTotal,
    decimal ConsumeRate, decimal DiscountTotal);

/// <summary>查营销效果报表（券部分）。</summary>
/// <param name="Range">时间范围档位。</param>
/// <param name="MerchantId">商户 Id，0 表示不限。</param>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
public record QueryCouponReportCommand(int Range = 3, long MerchantId = 0, long PlatformId = 0)
    : IRequest<ApiResponse<CouponReport>>;

/// <summary>营销效果报表处理器。</summary>
public sealed class CouponReportHandler
    : IRequestHandler<QueryCouponReportCommand, ApiResponse<CouponReport>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public CouponReportHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <inheritdoc />
    public async Task<ApiResponse<CouponReport>> Handle(
        QueryCouponReportCommand request, CancellationToken ct)
    {
        // 与订单 / 积分报表共用 Collaboration 里的那一份区间口径：
        // 三个服务的「今日」必须指同一天，否则运营同时开两个报表页会看到矛盾的数。
        var (from, to) = ReportRanges.Resolve(request.Range, DateTime.UtcNow);

        var agg = await _coupons.AggregateAsync(
            from, to, request.MerchantId, request.PlatformId, ct).ConfigureAwait(false);

        var report = new CouponReport(
            request.Range,
            ReportRanges.NameOf(request.Range),
            from,
            to,
            agg.IssuedTotal,
            agg.ReceivedTotal,
            agg.ConsumedTotal,
            agg.ConsumeRate,
            agg.DiscountTotal);

        return ApiResults.Ok(report);
    }
}
