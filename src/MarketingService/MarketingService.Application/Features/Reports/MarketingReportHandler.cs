using Collaboration.Domain.Common;
using Collaboration.Domain.Services;
using MarketingService.Domain.IRepository;
using MarketingService.Domain.Services;
using MediatR;

namespace MarketingService.Application.Features.Reports;

/// <summary>活动报表的一行。</summary>
/// <param name="ActivityId">活动 Id。</param>
/// <param name="ActivityName">活动名快照。</param>
/// <param name="OrderCount">
/// 参与订单数：<b>下单即计</b>（含未支付 / 已取消）。
/// 这样下钻出来的行数与它逐行对得上；金额那一列只算已支付。
/// </param>
/// <param name="OrderAmount">参与金额：这些订单的实付合计（已支付、未取消、未退款）。</param>
/// <param name="DiscountTotal">折扣总额：这些订单因该活动实际让利合计。</param>
public sealed record ActivityReportRow(
    long ActivityId, string ActivityName,
    long OrderCount, decimal OrderAmount, decimal DiscountTotal);

/// <summary>营销效果报表（BUSINESS.md 17）：活动 + 券。</summary>
/// <param name="Range">时间范围档位：1 今日 / 2 昨日 / 3 近 7 天 / 4 近 30 天。</param>
/// <param name="RangeName">时间范围中文名。</param>
/// <param name="From">区间起（含）。</param>
/// <param name="To">区间止（不含）。</param>
/// <param name="Activities">逐活动明细，也是「下钻订单明细」的入口（按 activityId 查参与记录）。</param>
/// <param name="ActivityOrderCount">活动参与订单数合计。</param>
/// <param name="ActivityOrderAmount">活动参与金额合计。</param>
/// <param name="ActivityDiscountTotal">活动折扣总额合计。</param>
/// <param name="IssuedTotal">券发放总数：运营配的库存总量。</param>
/// <param name="ReceivedTotal">券领取数：区间内客户实际领走。</param>
/// <param name="ConsumedTotal">券核销数：区间内真正用掉。</param>
/// <param name="ConsumeRate">核销率 = 核销数 / 领取数，以 0~1 小数下发。</param>
/// <param name="DiscountTotal">券折扣总额：核销掉的券实际让利合计（两位小数）。</param>
/// <remarks>
/// <b>活动与券是并列的两段</b>（BUSINESS.md 11.1）：活动不需要领取、下单即生效；
/// 券要先领、订单级只能一张。合成一个「优惠总额」会让运营分不清
/// 「没人参加活动」和「券发了没人用」，那是两种完全不同的处置。
/// </remarks>
public sealed record MarketingReport(
    int Range, string RangeName, DateTime From, DateTime To,
    IReadOnlyList<ActivityReportRow> Activities,
    long ActivityOrderCount, decimal ActivityOrderAmount, decimal ActivityDiscountTotal,
    long IssuedTotal, long ReceivedTotal, long ConsumedTotal,
    decimal ConsumeRate, decimal DiscountTotal,
    bool ActivitiesTruncated);

/// <summary>查营销效果报表（活动 + 券）。</summary>
/// <param name="Range">时间范围档位。</param>
/// <param name="MerchantId">商户 Id，0 表示不限。</param>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
public record QueryMarketingReportCommand(int Range = 3, long MerchantId = 0, long PlatformId = 0)
    : IRequest<ApiResponse<MarketingReport>>;

/// <summary>营销效果报表处理器。</summary>
public sealed class MarketingReportHandler
    : IRequestHandler<QueryMarketingReportCommand, ApiResponse<MarketingReport>>
{
    /// <summary>一次最多回多少个订单号去订单服务取金额。</summary>
    /// <remarks>
    /// 订单号是整段发过去的，不封顶的话一个 30 天的区间可能带上几万个单号，
    /// 请求体与订单侧的 IN 查询都会失控。超出部分按参与订单数倒序取头部活动
    /// （报表本来就按参与量排序，头部活动的金额才是运营真正在看的）。
    /// </remarks>
    private const int MaxOrderNosForAmount = 5000;

    private readonly IPromotionRepository _promotions;
    private readonly ICouponRepository _coupons;
    private readonly IOrderAmountPort _orders;

    /// <summary>构造处理器。</summary>
    /// <param name="promotions">活动仓储。</param>
    /// <param name="coupons">券仓储。</param>
    /// <param name="orders">订单金额端口。</param>
    public MarketingReportHandler(
        IPromotionRepository promotions, ICouponRepository coupons, IOrderAmountPort orders)
    {
        _promotions = promotions;
        _coupons = coupons;
        _orders = orders;
    }

    /// <inheritdoc />
    public async Task<ApiResponse<MarketingReport>> Handle(
        QueryMarketingReportCommand request, CancellationToken ct)
    {
        // 与订单 / 积分报表共用 Collaboration 里的那一份区间口径：
        // 三个服务的「今日」必须指同一天，否则运营同时开两个报表页会看到矛盾的数。
        var (from, to) = ReportRanges.Resolve(request.Range, DateTime.UtcNow);

        var participation = await _promotions.AggregateParticipationAsync(
            from, to, request.MerchantId, request.PlatformId, limit: 100, ct).ConfigureAwait(false);
        var aggregates = participation.Items;

        // 每个活动单独问一次订单服务，而不是把全部单号合起来问一次：
        // 合并只能拿到一个总额，拆不回各活动，而报表要的是逐活动的「参与金额」。
        var rows = new List<ActivityReportRow>(aggregates.Count);
        foreach (var agg in aggregates)
        {
            var orderNos = agg.OrderNos.Take(MaxOrderNosForAmount).ToList();
            var amount = await _orders.SumPayableAsync(orderNos, ct).ConfigureAwait(false);

            rows.Add(new ActivityReportRow(
                agg.ActivityId, agg.ActivityName, agg.OrderCount, amount, agg.DiscountTotal));
        }

        var couponAgg = await _coupons.AggregateAsync(
            from, to, request.MerchantId, request.PlatformId, ct).ConfigureAwait(false);

        var report = new MarketingReport(
            request.Range,
            ReportRanges.NameOf(request.Range),
            from,
            to,
            rows,
            rows.Sum(a => a.OrderCount),
            Math.Round(rows.Sum(a => a.OrderAmount), 2, MidpointRounding.AwayFromZero),
            Math.Round(rows.Sum(a => a.DiscountTotal), 2, MidpointRounding.AwayFromZero),
            couponAgg.IssuedTotal,
            couponAgg.ReceivedTotal,
            couponAgg.ConsumedTotal,
            couponAgg.ConsumeRate,
            couponAgg.DiscountTotal,
            // 活动数超过上限时**明确告诉前端**：否则运营看到的是一份不完整却毫无提示的名单
            ActivitiesTruncated: participation.TotalActivities > aggregates.Count);

        return ApiResults.Ok(report);
    }
}
