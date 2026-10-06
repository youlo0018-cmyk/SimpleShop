using Collaboration.Domain.Common;
using MediatR;
using OrderService.Domain.Ports;
using Collaboration.Domain.Services;

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
            lowStockCount,
            BuildMetrics(gmv, agg.OrderCount, agg.PaidOrderCount, agg.CompletedOrderCount,
                avgOrderValue, refund, refundRate, lowStockCount));

        return ApiResults.Ok(report);
    }

    /// <summary>组装工作台的指标卡（文案由后端下发，前端不写死）。</summary>
    /// <param name="gmv">成交额。</param>
    /// <param name="orderCount">订单数。</param>
    /// <param name="paidOrderCount">支付订单数。</param>
    /// <param name="completedOrderCount">完成订单数。</param>
    /// <param name="avgOrderValue">客单价。</param>
    /// <param name="refund">退款金额。</param>
    /// <param name="refundRate">退款率（小数）。</param>
    /// <param name="lowStockCount">库存预警数。</param>
    /// <returns>指标卡清单，顺序即展示顺序。</returns>
    private static IReadOnlyList<ReportMetric> BuildMetrics(
        decimal gmv, long orderCount, long paidOrderCount, long completedOrderCount,
        decimal avgOrderValue, decimal refund, decimal refundRate, int lowStockCount)
        =>
        [
            new("gmv", "成交额", gmv.ToString("N2"), "元"),
            new("orderCount", "订单数", orderCount.ToString("N0"), "单"),
            new("paidOrderCount", "支付订单", paidOrderCount.ToString("N0"), "单"),
            new("completedOrderCount", "完成订单", completedOrderCount.ToString("N0"), "单"),
            new("avgOrderValue", "客单价", avgOrderValue.ToString("N2"), "元"),
            new("refundAmount", "退款金额", refund.ToString("N2"), "元"),
            // 比率在这里就转成百分数**字符串**：4.6 要求数值以小数下发，
            // 但展示层统一由后端给出，免得两个页面各写一套百分比格式化
            new("refundRate", "退款率", (refundRate * 100m).ToString("N2") + "%", ""),
            new("lowStockCount", "库存预警", lowStockCount.ToString("N0"), "个"),
        ];

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
