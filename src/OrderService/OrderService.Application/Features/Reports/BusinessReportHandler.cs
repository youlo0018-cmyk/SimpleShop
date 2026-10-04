using Collaboration.Domain.Common;
using MediatR;
using OrderService.Domain.Ports;
using OrderService.Domain.Services;

namespace OrderService.Application.Features.Reports;

/// <summary>查工作台经营报表。</summary>
/// <param name="Range">时间范围档位：1 今日 / 2 昨日 / 3 近 7 天 / 4 近 30 天。</param>
/// <param name="MerchantId">商户 Id，0 表示平台视角看全部。</param>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
public record QueryBusinessReportCommand(int Range = 3, long MerchantId = 0, long PlatformId = 0)
    : IRequest<ApiResponse<BusinessReport>>;

/// <summary>工作台经营报表处理器。</summary>
public sealed class BusinessReportHandler
    : IRequestHandler<QueryBusinessReportCommand, ApiResponse<BusinessReport>>
{
    private readonly IOrderStore _store;
    private readonly IRefundStatsPort _refunds;
    private readonly ILowStockPort _lowStock;

    /// <summary>构造处理器。</summary>
    /// <param name="store">订单仓储。</param>
    /// <param name="refunds">退款统计端口。</param>
    /// <param name="lowStock">库存预警数端口。</param>
    public BusinessReportHandler(
        IOrderStore store, IRefundStatsPort refunds, ILowStockPort lowStock)
    {
        _store = store;
        _refunds = refunds;
        _lowStock = lowStock;
    }

    /// <inheritdoc />
    public async Task<ApiResponse<BusinessReport>> Handle(
        QueryBusinessReportCommand request, CancellationToken ct)
    {
        var (from, to) = ReportRanges.Resolve(request.Range, DateTime.UtcNow);

        var agg = await _store.AggregateAsync(
            from, to, request.MerchantId, request.PlatformId, ct).ConfigureAwait(false);

        // 这两个是跨服务的附属指标，端口内部已经「失败返回 0」，
        // 这里不再 try——再包一层只会把真实故障的日志也吞掉。
        var refundAmount = await _refunds.SumApprovedAsync(
            from, to, request.MerchantId, request.PlatformId, ct).ConfigureAwait(false);

        var lowStockCount = await _lowStock
            .CountLowStockAsync(request.MerchantId, request.PlatformId, ct).ConfigureAwait(false);

        var gmv = Round2(agg.Gmv);
        var refund = Round2(refundAmount);

        // 分母为 0 时返回 0 而不是抛异常或返回 Infinity：
        // 「今天一单都没有」是正常情况，页面要显示 0.00，
        // 不能显示 NaN 让运营以为系统出问题了。
        var avgOrderValue = agg.PaidOrderCount > 0
            ? Round2(gmv / agg.PaidOrderCount)
            : 0m;

        var refundRate = gmv > 0m
            ? Math.Round(refund / gmv, 4, MidpointRounding.AwayFromZero)
            : 0m;

        var report = new BusinessReport(
            request.Range,
            ReportRanges.NameOf(request.Range),
            from,
            to,
            gmv,
            agg.OrderCount,
            agg.PaidOrderCount,
            agg.CompletedOrderCount,
            avgOrderValue,
            refund,
            refundRate,
            lowStockCount);

        return ApiResults.Ok(report);
    }

    /// <summary>金额统一保留两位小数、四舍五入远离零。</summary>
    /// <param name="value">原值。</param>
    /// <returns>两位小数的金额。</returns>
    /// <remarks>
    /// 全项目口径（BUSINESS.md 8.4）：<c>Math.Round</c> 默认是<b>银行家舍入</b>
    /// （0.125 → 0.12），与「四舍五入」差一分钱，必须显式给
    /// <see cref="MidpointRounding.AwayFromZero"/>。
    /// </remarks>
    private static decimal Round2(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
