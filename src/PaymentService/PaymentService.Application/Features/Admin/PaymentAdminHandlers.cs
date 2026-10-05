using Collaboration.Domain.Common;
using MediatR;
using PaymentService.Domain.Entities;
using PaymentService.Domain.IRepository;

namespace PaymentService.Application.Features.Admin;

/// <summary>后台支付单分页处理器。</summary>
public sealed class QueryAdminPaymentsHandler
    : IRequestHandler<QueryAdminPaymentsCommand, ApiResponse<PagedResult<AdminPaymentDto>>>
{
    private readonly IPaymentRepository _payments;

    /// <summary>构造处理器。</summary>
    /// <param name="payments">支付仓储。</param>
    public QueryAdminPaymentsHandler(IPaymentRepository payments) => _payments = payments;

    /// <inheritdoc />
    /// <remarks>
    /// 租户范围用请求里的 PlatformId / MerchantId，而不是从令牌上下文取 ——
    /// 后台超管需要跨平台查看，所以这里允许显式传范围（0 = 不限），
    /// 范围收窄由网关的租户上下文在更上层决定。
    /// </remarks>
    public async Task<ApiResponse<PagedResult<AdminPaymentDto>>> Handle(
        QueryAdminPaymentsCommand request, CancellationToken ct)
    {
        var page = await _payments.PageAsync(
            request.Status, request.Keyword, request.PlatformId, request.MerchantId,
            request.Page, request.PageSize, ct).ConfigureAwait(false);

        var dtos = page.Items.Select(p => new AdminPaymentDto(
            p.Id, p.PaymentNo, p.OrderNo, p.Amount,
            p.Status, PaymentStatuses.NameOf(p.Status),
            p.Channel, PaymentChannels.NameOf(p.Channel),
            string.IsNullOrWhiteSpace(p.FailReason) ? null : p.FailReason,
            p.PaidAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            p.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"))).ToList();

        return ApiResults.Ok(new PagedResult<AdminPaymentDto>(dtos, page.Total, page.Page, page.PageSize));
    }
}

/// <summary>后台退款单详情处理器。</summary>
public sealed class QueryAdminRefundDetailHandler
    : IRequestHandler<QueryAdminRefundDetailCommand, ApiResponse<AdminRefundDetailDto>>
{
    private readonly IRefundRepository _refunds;

    /// <summary>构造处理器。</summary>
    /// <param name="refunds">退款仓储。</param>
    public QueryAdminRefundDetailHandler(IRefundRepository refunds) => _refunds = refunds;

    /// <inheritdoc />
    /// <remarks>
    /// 详情**不做客户归属校验** —— 后台的可见范围由网关租户上下文 + 权限点决定；
    /// 归属校验回答的是「这笔单是不是你的」，那是 C 端的事（与后台订单详情同理）。
    /// </remarks>
    public async Task<ApiResponse<AdminRefundDetailDto>> Handle(
        QueryAdminRefundDetailCommand request, CancellationToken ct)
    {
        var refund = await _refunds.GetByIdAsync(request.RefundId, ct).ConfigureAwait(false);

        if (refund is null)
        {
            return ApiResults.Fail<AdminRefundDetailDto>(BaseApiResponseCode.NotFound, "退款单不存在");
        }

        var items = await _refunds.ListItemsAsync(refund.Id, ct).ConfigureAwait(false);

        var dto = new AdminRefundDetailDto(
            refund.Id, refund.RefundNo, refund.OrderNo, refund.CustomerName,
            refund.Amount,
            refund.RefundType, RefundTypes.NameOf(refund.RefundType),
            refund.Status, RefundStatuses.NameOf(refund.Status),
            refund.Reason,
            string.IsNullOrWhiteSpace(refund.RejectReason) ? "" : refund.RejectReason,
            refund.ApproverName,
            refund.ApprovedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            refund.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            items.Select(a => new AdminRefundItemDto(
                a.OrderItemId, a.SkuId, a.ProductName, a.SkuSpecText, a.Quantity, a.Amount)).ToList());

        return ApiResults.Ok(dto);
    }
}
