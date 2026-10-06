using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using MediatR;
using PaymentService.Application.Services;
using PaymentService.Domain.Entities;
using PaymentService.Domain.IRepository;
using PaymentService.Domain.Services;
using Microsoft.Extensions.Logging;

namespace PaymentService.Application.Features.Refund;

/// <summary>退款审批通过处理器。</summary>
public sealed class ApproveRefundHandler : IRequestHandler<ApproveRefundCommand, ApiResponse>
{
    private readonly IRefundRepository _refunds;
    private readonly IOrderPort _orders;
    private readonly ILogger<ApproveRefundHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="refunds">退款仓储。</param>
    /// <param name="orders">订单服务端口。</param>
    /// <param name="logger">日志器。</param>
    public ApproveRefundHandler(IRefundRepository refunds, IOrderPort orders, ILogger<ApproveRefundHandler> logger)
    {
        _refunds = refunds;
        _orders = orders;
        _logger = logger;
    }

    /// <summary>执行审批通过。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// <b>审批时再校验一次「累计退款 ≤ 实付」</b>（规格 5.25）：申请到审批之间可能又批了一单。
    /// 只在申请时校验的话，两笔并发申请都能通过校验，审批后累计退款就超过实付了。
    /// </remarks>
    public async Task<ApiResponse> Handle(ApproveRefundCommand request, CancellationToken ct)
    {
        // 🔴 审批人从**令牌租户上下文**取，不从请求体取。
        // 之前是 request.ApproverId / request.ApproverName —— 等于调用方自称谁是审批人，
        // 而「退款单审批人」是财务审计凭据，伪造它等于伪造审计记录。
        var ctx = TenantContextHolder.Current;

        if (ctx.UserId <= 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.Unauthorized, "登录状态已失效，请重新登录");
        }

        var refund = await _refunds.GetByIdAsync(request.RefundId, ct).ConfigureAwait(false);
        if (refund is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "退款单不存在");

        if (refund.Status != RefundStatuses.PendingApproval)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BusinessError, $"该退款单已处理（{RefundStatuses.NameOf(refund.Status)}）");
        }

        var order = await _orders.GetForPaymentAsync(refund.OrderNo, ct).ConfigureAwait(false);
        if (order is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "订单不存在");

        // 审批时**不能**把待审批的算进来：那会把本单自己算进去，
        // 变成「这笔退款永远超过它自己」而永远批不过
        var refundedItems = await _refunds.ListRefundedItemsAsync(refund.OrderNo, false, ct).ConfigureAwait(false);
        var alreadyRefunded = refundedItems.Sum(a => a.Amount);

        if (!RefundRules.WithinRefundableBalance(refund.Amount, order.PayableAmount, alreadyRefunded))
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BusinessError,
                $"累计退款将超过订单实付：本次 {refund.Amount:0.00} 元，" +
                $"已退 {alreadyRefunded:0.00} 元，实付 {order.PayableAmount:0.00} 元");
        }

        var changed = await _refunds.TryApproveAsync(
            refund.Id, RefundStatuses.PendingApproval, RefundStatuses.Refunded,
            ctx.UserId, ctx.UserName, string.Empty, ct).ConfigureAwait(false);

        if (changed == 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BusinessError, "该退款单已被其他人处理");
        }

        // 把本次退款的**行明细**一起送过去：订单侧要用它写自己的退款台账
        // （后台订单详情的「行级可退余额」就是从那张表算的）。
        // 只送金额的话，订单侧只能瞎猜退的是哪几行。
        var refundLines = await _refunds.ListItemsAsync(refund.Id, ct).ConfigureAwait(false);
        var lineSnapshots = refundLines
            .Select(a => new RefundLineSnapshot(a.OrderItemId, a.Quantity, a.Amount))
            .ToList();

        var marked = await _orders
            .MarkRefundedAsync(refund.OrderNo, refund.Amount, lineSnapshots, ct)
            .ConfigureAwait(false);
        if (!marked)
        {
            // 退款单已转「已退款」但订单没转：这是**不一致状态**，必须显式报错让人来查。
            // 悄悄返回成功会让「钱退了但订单还在」被当成正常，事后很难发现
            _logger.LogError(
                "退款单 {RefundNo} 已审批，但订单 {OrderNo} 标记已退款失败，需人工核对",
                refund.RefundNo, refund.OrderNo);
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.RemoteCallFailed, "退款单已审批，但订单状态更新失败，请联系管理员核对");
        }

        return ApiResponseFactory.Ok("退款已生效");
    }
}
