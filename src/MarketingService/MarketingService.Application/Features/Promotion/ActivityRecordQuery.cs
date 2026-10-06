using Collaboration.Domain.Common;
using Collaboration.Domain.Services;
using FluentValidation;
using MarketingService.Domain.IRepository;
using MediatR;

namespace MarketingService.Application.Features.Promotion;

/// <summary>活动参与记录的一行（报表下钻订单明细）。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="CustomerId">下单客户 Id。</param>
/// <param name="DiscountAmount">本单因该活动让利多少。</param>
/// <param name="CreatedAt">参与时间（Asia/Shanghai，展示用）。</param>
public sealed record ActivityRecordItem(
    string OrderNo, long CustomerId, decimal DiscountAmount, string CreatedAt);

/// <summary>分页查活动参与记录（营销效果报表下钻）。</summary>
/// <param name="ActivityId">活动 Id，0 表示不限。</param>
/// <param name="Range">时间范围档位：1 今日 / 2 昨日 / 3 近 7 天 / 4 近 30 天。</param>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryActivityRecordsCommand(
    long ActivityId = 0, int Range = 3, int Page = 1, int PageSize = 20)
    : IRequest<ApiResponse<PagedResult<ActivityRecordItem>>>;

/// <summary>活动参与记录处理器。</summary>
public sealed class QueryActivityRecordsHandler
    : IRequestHandler<QueryActivityRecordsCommand, ApiResponse<PagedResult<ActivityRecordItem>>>
{
    private readonly IPromotionRepository _promotions;

    /// <summary>构造处理器。</summary>
    /// <param name="promotions">活动仓储。</param>
    public QueryActivityRecordsHandler(IPromotionRepository promotions) => _promotions = promotions;

    /// <summary>执行分页。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当页参与记录。</returns>
    /// <remarks>
    /// 时间口径与报表**完全一致**（<see cref="ReportRanges"/>）：下钻出来的行数必须
    /// 与报表上的「参与订单数」对得上，否则运营会以为数据丢了。
    /// </remarks>
    public async Task<ApiResponse<PagedResult<ActivityRecordItem>>> Handle(
        QueryActivityRecordsCommand request, CancellationToken ct)
    {
        var (from, to) = ReportRanges.Resolve(request.Range, DateTime.UtcNow);

        var (items, total) = await _promotions.PageParticipationAsync(
            request.ActivityId, from, to, request.Page, request.PageSize, ct).ConfigureAwait(false);

        var dtos = items.Select(a => new ActivityRecordItem(
            a.OrderNo,
            a.CustomerId,
            a.DiscountAmount,
            // 与其余后台列表同一口径：库里存 UTC，展示转服务器本地时区（DATA_SPEC 2.8）
            DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc)
                .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")))
            .ToList();

        return ApiResults.Ok(new PagedResult<ActivityRecordItem>(
            dtos, total, request.Page, request.PageSize));
    }
}

/// <summary>活动参与记录校验。</summary>
public sealed class QueryActivityRecordsValidator : AbstractValidator<QueryActivityRecordsCommand>
{
    /// <summary>构造校验器。</summary>
    public QueryActivityRecordsValidator()
    {
        RuleFor(x => x.ActivityId).GreaterThanOrEqualTo(0).WithMessage("活动 Id 不正确");
        RuleFor(x => x.Range).InclusiveBetween(1, 4).WithMessage("报表时间范围不正确");
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须为正数");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("每页条数不正确");
    }
}
