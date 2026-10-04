using Collaboration.Domain.Common;
using MediatR;
using PaymentService.Domain.Entities;
using PaymentService.Domain.IRepository;

namespace PaymentService.Application.Features.Refund;

/// <summary>退款拒绝处理器。</summary>
public sealed class RejectRefundHandler : IRequestHandler<RejectRefundCommand, ApiResponse>
{
    private readonly IRefundRepository _refunds;

    /// <summary>构造处理器。</summary>
    /// <param name="refunds">退款仓储。</param>
    public RejectRefundHandler(IRefundRepository refunds) => _refunds = refunds;

    /// <summary>执行拒绝。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks><b>拒绝无任何副作用</b>：订单、库存、积分都不动（规格 10.2）。</remarks>
    public async Task<ApiResponse> Handle(RejectRefundCommand request, CancellationToken ct)
    {
        var refund = await _refunds.GetByIdAsync(request.RefundId, ct).ConfigureAwait(false);
        if (refund is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "退款单不存在");

        if (refund.Status != RefundStatuses.PendingApproval)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BusinessError, $"该退款单已处理（{RefundStatuses.NameOf(refund.Status)}）");
        }

        var changed = await _refunds.TryApproveAsync(
            refund.Id, RefundStatuses.PendingApproval, RefundStatuses.Rejected,
            request.ApproverId, request.ApproverName, request.RejectReason.Trim(), ct).ConfigureAwait(false);

        return changed > 0
            ? ApiResponseFactory.Ok("已拒绝该退款申请")
            : ApiResponseFactory.Fail(BaseApiResponseCode.BusinessError, "该退款单已被其他人处理");
    }
}

/// <summary>退款单查询处理器。</summary>
public sealed class QueryRefundsHandler : IRequestHandler<QueryRefundsCommand, ApiResponse<PagedRefundDtos>>
{
    private readonly IRefundRepository _refunds;

    /// <summary>构造处理器。</summary>
    /// <param name="refunds">退款仓储。</param>
    public QueryRefundsHandler(IRefundRepository refunds) => _refunds = refunds;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>退款单分页。</returns>
    public async Task<ApiResponse<PagedRefundDtos>> Handle(QueryRefundsCommand request, CancellationToken ct)
    {
        var page = await _refunds.PageAsync(
            request.Status, request.OrderNo, request.Page, request.PageSize, ct);

        var items = new List<RefundDto>(page.Items.Count);
        foreach (var refund in page.Items)
        {
            var details = await _refunds.ListItemsAsync(refund.Id, ct);
            items.Add(new RefundDto(
                refund.Id, refund.RefundNo, refund.OrderNo, refund.Amount,
                refund.RefundType, RefundTypes.NameOf(refund.RefundType),
                refund.Status, RefundStatuses.NameOf(refund.Status),
                refund.Reason, refund.RejectReason, refund.ApproverName,
                RefundAssembler.FormatTime(refund.CreatedAt),
                details.Select(a => new RefundItemDto(
                    a.OrderItemId, a.ProductName, a.SkuSpecText, a.Amount)).ToList()));
        }

        return ApiResults.Ok(new PagedRefundDtos(items, page.Total, page.Page, page.PageSize));
    }
}

/// <summary>退款模块的展示装配。</summary>
public static class RefundAssembler
{
    /// <summary>把 UTC 时间转成展示字符串。</summary>
    /// <param name="utc">UTC 时间。</param>
    /// <returns>展示用字符串。</returns>
    public static string FormatTime(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
