using Collaboration.Domain.Common;
using Collaboration.Domain.Services;
using MarketingService.Domain.IRepository;
using MarketingService.Domain.Services;
using MediatR;

namespace MarketingService.Application.Features.Reports;

/// <summary>查秒杀效果报表（BUSINESS.md 17）。</summary>
/// <param name="Range">时间范围档位：1 今日 / 2 昨日 / 3 近 7 天 / 4 近 30 天。</param>
/// <param name="SessionId">只看某个场次，0 表示全部。</param>
/// <param name="MerchantId">商户 Id，0 表示不限。</param>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
public record QuerySeckillReportCommand(int Range = 3, long SessionId = 0, long MerchantId = 0, long PlatformId = 0)
    : IRequest<ApiResponse<SeckillReport>>;

/// <summary>秒杀效果报表处理器。</summary>
public sealed class SeckillReportHandler
    : IRequestHandler<QuerySeckillReportCommand, ApiResponse<SeckillReport>>
{
    private readonly ISeckillRepository _seckill;
    private readonly ISeckillGmvPort _gmv;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    /// <param name="gmv">成交额端口（订单服务）。</param>
    public SeckillReportHandler(ISeckillRepository seckill, ISeckillGmvPort gmv)
    {
        _seckill = seckill;
        _gmv = gmv;
    }

    /// <inheritdoc />
    public async Task<ApiResponse<SeckillReport>> Handle(
        QuerySeckillReportCommand request, CancellationToken ct)
    {
        // 场次按「开始时间」落在区间内筛。和工作台 GMV 按支付时间筛不同：
        // 秒杀报表问的是「这段时间开的场次表现如何」，而不是「这段时间成交了多少钱」
        // ——后者的钱已经算进工作台 GMV 了。
        var (from, to) = ReportRanges.Resolve(request.Range, DateTime.UtcNow);

        var sessions = await _seckill.AggregateSessionsAsync(
            from, to, request.SessionId, request.MerchantId, request.PlatformId,
            limit: 200, ct).ConfigureAwait(false);

        // 一次性把所有成功订单号交给订单服务换 GMV，而不是每个场次调一次
        var allOrderNos = sessions.SelectMany(a => a.OrderNos).Distinct().ToList();

        var totalGmv = await _gmv.SumPayableAsync(allOrderNos, ct).ConfigureAwait(false);

        var totalParticipants = sessions.Sum(a => a.ParticipantCount);
        var totalSuccess = sessions.Sum(a => a.GrabSuccessCount);

        return ApiResults.Ok(new SeckillReport(
            sessions, totalParticipants, totalSuccess, totalGmv));
    }
}
