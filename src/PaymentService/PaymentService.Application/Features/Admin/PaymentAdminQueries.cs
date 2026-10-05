using Collaboration.Domain.Common;
using MediatR;
using PaymentService.Domain.Entities;
using PaymentService.Domain.IRepository;

namespace PaymentService.Application.Features.Admin;

/// <summary>后台支付单分页。</summary>
/// <param name="Status">状态过滤，0 表示不限。</param>
/// <param name="Keyword">按支付单号 / 订单号模糊匹配。</param>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
/// <param name="MerchantId">商户 Id，0 表示不限。</param>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryAdminPaymentsCommand(
    int Status = 0, string Keyword = "", long PlatformId = 0, long MerchantId = 0,
    int Page = 1, int PageSize = 20)
    : IRequest<ApiResponse<PagedResult<AdminPaymentDto>>>;

/// <summary>后台退款单详情。</summary>
/// <param name="RefundId">退款单 Id。</param>
public record QueryAdminRefundDetailCommand(long RefundId)
    : IRequest<ApiResponse<AdminRefundDetailDto>>;

/// <summary>后台支付单列表行。</summary>
/// <param name="PaymentId">支付单 Id。</param>
/// <param name="PaymentNo">支付单号。</param>
/// <param name="OrderNo">订单号。</param>
/// <param name="Amount">支付金额。</param>
/// <param name="Status">状态值。</param>
/// <param name="StatusName">状态中文名。</param>
/// <param name="Channel">支付渠道值。</param>
/// <param name="ChannelName">渠道中文名。</param>
/// <param name="FailReason">失败原因。</param>
/// <param name="PaidAt">支付时间，未支付为空。</param>
/// <param name="CreatedAt">创建时间。</param>
public sealed record AdminPaymentDto(
    long PaymentId, string PaymentNo, string OrderNo, decimal Amount,
    int Status, string StatusName, int Channel, string ChannelName,
    string? FailReason, string? PaidAt, string CreatedAt);

/// <summary>后台退款单详情（含退到哪一行）。</summary>
/// <param name="RefundId">退款单 Id。</param>
/// <param name="RefundNo">退款单号。</param>
/// <param name="OrderNo">订单号。</param>
/// <param name="CustomerName">客户昵称快照。</param>
/// <param name="Amount">退款金额。</param>
/// <param name="RefundType">退款类型值。</param>
/// <param name="RefundTypeName">退款类型中文名。</param>
/// <param name="Status">状态值。</param>
/// <param name="StatusName">状态中文名。</param>
/// <param name="Reason">申请原因。</param>
/// <param name="RejectReason">拒绝原因。</param>
/// <param name="ApproverName">审批人。</param>
/// <param name="ApprovedAt">审批时间。</param>
/// <param name="CreatedAt">申请时间。</param>
/// <param name="Items">退款明细行。</param>
public sealed record AdminRefundDetailDto(
    long RefundId, string RefundNo, string OrderNo, string CustomerName,
    decimal Amount, int RefundType, string RefundTypeName, int Status, string StatusName,
    string Reason, string RejectReason, string ApproverName, string? ApprovedAt, string CreatedAt,
    IReadOnlyList<AdminRefundItemDto> Items);

/// <summary>退款明细行。</summary>
/// <param name="OrderItemId">原订单行 Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="ProductName">商品名快照。</param>
/// <param name="SkuSpecText">规格文本快照。</param>
/// <param name="Quantity">退货数量。</param>
/// <param name="Amount">该行退款金额。</param>
public sealed record AdminRefundItemDto(
    long OrderItemId, long SkuId, string ProductName, string SkuSpecText, int Quantity, decimal Amount);
