using Collaboration.Domain.Common;
using MediatR;
using PointService.Domain.Entities;
using PointService.Domain.IRepository;

namespace PointService.Application.Features.Admin;

/// <summary>后台分页查积分流水（跨客户）处理器。</summary>
public sealed class QueryAdminPointRecordsHandler
    : IRequestHandler<QueryAdminPointRecordsCommand, ApiResponse<PagedResult<AdminPointRecordItem>>>
{
    private readonly IPointRepository _points;

    /// <summary>构造处理器。</summary>
    /// <param name="points">积分仓储。</param>
    public QueryAdminPointRecordsHandler(IPointRepository points) => _points = points;

    /// <inheritdoc />
    /// <remarks>
    /// <b>变动前后的余额直接从流水行读</b>（表里冗余了），不回表重算：
    /// 回表只能算出「现在的余额」，算不出「当时还剩多少」，
    /// 而后台排查客诉问的恰恰是后者（「我昨天还有 500 分，怎么现在只剩 300」）。
    ///
    /// <para>动作文案走 <see cref="PointActions.NameOf"/>（DATA_SPEC 4.5 枚举文案由后端下发），
    /// 不让前端维护对照表。</para>
    /// </remarks>
    public async Task<ApiResponse<PagedResult<AdminPointRecordItem>>> Handle(
        QueryAdminPointRecordsCommand request, CancellationToken ct)
    {
        var page = await _points.QueryRecordsAdminAsync(
            request.Page, request.PageSize, request.CustomerId,
            request.Action, request.BizNo, request.Keyword, ct).ConfigureAwait(false);

        var items = page.Items.Select(a => new AdminPointRecordItem(
            a.Id, a.CustomerId, a.BizNo, a.Action, PointActions.NameOf(a.Action),
            a.Quantity, a.BeforeAvailable, a.AfterAvailable,
            a.BeforeFrozen, a.AfterFrozen,
            a.LotExpireAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            a.Remark,
            a.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"))).ToList();

        return ApiResults.Ok(
            new PagedResult<AdminPointRecordItem>(items, page.Total, request.Page, request.PageSize));
    }
}
