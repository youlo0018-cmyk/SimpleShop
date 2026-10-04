using Collaboration.Domain.Common;
using MediatR;

namespace PaymentService.Application.Features.Refund;

/// <summary>退款申请的一行。</summary>
/// <param name="OrderItemId">订单行 Id。</param>
/// <param name="Amount">该行退款金额，必须大于 0。</param>
public sealed record RefundItemInput(long OrderItemId, decimal Amount);

/// <summary>代客申请退款（后台代客户发起）。</summary>
/// <param name="OrderId">订单 Id，只读，由订单行带出。</param>
/// <param name="OrderNo">订单号。</param>
/// <param name="Items">要退的行与金额。留空表示整单退。</param>
/// <param name="Reason">退款原因，2~200 字符。</param>
public record ApplyRefundCommand(long OrderId, string OrderNo, IReadOnlyList<RefundItemInput>? Items, string Reason) : IRequest<ApiResponse<long>>;

/// <summary>审批通过退款。</summary>
/// <param name="RefundId">退款单 Id。</param>
/// <param name="ApproverId">审批人 Id。</param>
/// <param name="ApproverName">审批人姓名。</param>
public record ApproveRefundCommand(long RefundId, long ApproverId, string ApproverName) : IRequest<ApiResponse>;

/// <summary>审批拒绝退款。</summary>
/// <param name="RefundId">退款单 Id。</param>
/// <param name="RejectReason">拒绝原因，2~200 字符。</param>
/// <param name="ApproverId">审批人 Id。</param>
/// <param name="ApproverName">审批人姓名。</param>
public record RejectRefundCommand(long RefundId, string RejectReason, long ApproverId, string ApproverName) : IRequest<ApiResponse>;

/// <summary>查退款单。</summary>
/// <param name="Status">状态过滤，0 表示不限。</param>
/// <param name="OrderNo">订单号过滤，空表示不限。</param>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryRefundsCommand(int Status = 0, string OrderNo = "", int Page = 1, int PageSize = 20) : IRequest<ApiResponse<PagedRefundDtos>>;

/// <summary>退款单视图。</summary>
/// <param name="RefundId">退款单 Id。</param>
/// <param name="RefundNo">退款单号。</param>
/// <param name="OrderNo">订单号。</param>
/// <param name="Amount">申请金额。</param>
/// <param name="RefundType">退款类型：1 整单 / 2 部分。</param>
/// <param name="RefundTypeName">退款类型中文名。</param>
/// <param name="Status">退款单状态。</param>
/// <param name="StatusName">退款单状态中文名。</param>
/// <param name="Reason">申请原因。</param>
/// <param name="RejectReason">拒绝原因。</param>
/// <param name="ApproverName">审批人。</param>
/// <param name="CreatedAt">申请时间。</param>
/// <param name="Items">退款明细。</param>
public sealed record RefundDto(long RefundId, string RefundNo, string OrderNo, decimal Amount, int RefundType, string RefundTypeName, int Status, string StatusName, string Reason, string RejectReason, string ApproverName, string CreatedAt, IReadOnlyList<RefundItemDto> Items);

/// <summary>退款明细视图。</summary>
/// <param name="OrderItemId">订单行 Id。</param>
/// <param name="ProductName">商品名快照。</param>
/// <param name="SkuSpecText">规格快照。</param>
/// <param name="Amount">该行退款金额。</param>
public sealed record RefundItemDto(long OrderItemId, string ProductName, string SkuSpecText, decimal Amount);

/// <summary>退款单分页结果。</summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record PagedRefundDtos(IReadOnlyList<RefundDto> Items, long Total, int Page, int PageSize);
