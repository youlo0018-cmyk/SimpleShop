using Collaboration.Domain.Common;
using Collaboration.Domain.Services;
using MediatR;
using PointService.Domain.IRepository;

namespace PointService.Application.Features.Reports;

/// <summary>积分报表（BUSINESS.md 17）。金额全为整数积分，没有小数问题。</summary>
/// <param name="Range">时间范围档位：1 今日 / 2 昨日 / 3 近 7 天 / 4 近 30 天。</param>
/// <param name="RangeName">时间范围中文名。</param>
/// <param name="From">区间起（含）。</param>
/// <param name="To">区间止（不含）。</param>
/// <param name="EarnedTotal">发放总额：区间内让可用积分增加的动作合计。</param>
/// <param name="ConsumedTotal">消耗总额：实扣，钱已付出、积分就此消失。</param>
/// <param name="ExpiredTotal">过期总额。</param>
/// <param name="LockedTotal">冻结总额：当前被下单占用的积分，尚未消耗。</param>
/// <param name="CurrentBalance">当前总余额：可用 + 冻结。</param>
public sealed record PointReport(
    int Range, string RangeName, DateTime From, DateTime To,
    long EarnedTotal, long ConsumedTotal, long ExpiredTotal,
    long LockedTotal, long CurrentBalance);

/// <summary>查积分报表。</summary>
/// <param name="Range">时间范围档位。</param>
public record QueryPointReportCommand(int Range = 3)
    : IRequest<ApiResponse<PointReport>>;

/// <summary>积分报表处理器。</summary>
public sealed class PointReportHandler
    : IRequestHandler<QueryPointReportCommand, ApiResponse<PointReport>>
{
    private readonly IPointRepository _points;

    /// <summary>构造处理器。</summary>
    /// <param name="points">积分仓储。</param>
    public PointReportHandler(IPointRepository points) => _points = points;

    /// <inheritdoc />
    public async Task<ApiResponse<PointReport>> Handle(
        QueryPointReportCommand request, CancellationToken ct)
    {
        // 用 Collaboration 里那一份区间口径：三个服务的「今日」必须指同一天，
        // 否则运营同时开着工作台与积分报表会看到两个不同含义的「今日」。
        var (from, to) = ReportRanges.Resolve(request.Range, DateTime.UtcNow);

        var agg = await _points.AggregateAsync(from, to, ct).ConfigureAwait(false);

        var report = new PointReport(
            request.Range,
            ReportRanges.NameOf(request.Range),
            from,
            to,
            agg.EarnedTotal,
            agg.ConsumedTotal,
            agg.ExpiredTotal,
            agg.LockedTotal,
            agg.CurrentBalance);

        return ApiResults.Ok(report);
    }
}
