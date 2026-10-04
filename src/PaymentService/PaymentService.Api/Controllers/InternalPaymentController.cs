using Collaboration.Domain.Common;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Domain.IRepository;

namespace PaymentService.Api.Controllers;

/// <summary>支付内部接口，供订单服务的工作台报表调用。网关不路由 /internal 前缀。</summary>
[ApiController]
[Route("internal/payments")]
public sealed class InternalPaymentController : ControllerBase
{
    private readonly IRefundRepository _refunds;

    /// <summary>构造控制器。</summary>
    /// <param name="refunds">退款仓储。</param>
    public InternalPaymentController(IRefundRepository refunds) => _refunds = refunds;

    /// <summary>按区间汇总退款金额（报表用）。</summary>
    /// <param name="command">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>退款金额合计。</returns>
    /// <remarks>
    /// 为什么退款统计在支付服务而不在订单服务：退款单在支付库，
    /// 订单服务跨库查不到，放过去只能让它把整段退款数据拉回来自己算。
    /// </remarks>
    [HttpPost("refunds/SumApproved")]
    public async Task<ApiResponse<RefundSumResult>> SumApproved(
        [FromBody] RefundSumQuery command, CancellationToken ct)
    {
        var amount = await _refunds.SumApprovedAmountAsync(
            command.From, command.To, command.MerchantId, command.PlatformId, ct)
            .ConfigureAwait(false);

        return ApiResults.Ok(new RefundSumResult(amount));
    }
}

/// <summary>退款汇总查询条件。</summary>
/// <param name="From">区间起（含）。</param>
/// <param name="To">区间止（不含）。</param>
/// <param name="MerchantId">商户 Id，0 表示不限。</param>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
public sealed record RefundSumQuery(
    DateTime From, DateTime To, long MerchantId = 0, long PlatformId = 0);

/// <summary>退款汇总结果。</summary>
/// <param name="Amount">审批通过的退款金额合计。</param>
public sealed record RefundSumResult(decimal Amount);
