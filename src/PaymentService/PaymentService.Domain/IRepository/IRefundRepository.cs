using PaymentService.Domain.Entities;

namespace PaymentService.Domain.IRepository;

/// <summary>退款仓储。</summary>
public interface IRefundRepository
{
    /// <summary>插入退款单与明细（同事务）。</summary>
    /// <param name="refund">退款单。</param>
    /// <param name="items">退款明细。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>退款单 Id。</returns>
    Task<long> InsertAsync(RefundOrder refund, IReadOnlyCollection<RefundOrderItem> items,
        CancellationToken ct = default);

    /// <summary>按 Id 取退款单。</summary>
    /// <param name="refundId">退款单 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>找到返回实体，否则 null。</returns>
    Task<RefundOrder?> GetByIdAsync(long refundId, CancellationToken ct = default);

    /// <summary>取退款单明细。</summary>
    /// <param name="refundId">退款单 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>明细列表。</returns>
    Task<IReadOnlyList<RefundOrderItem>> ListItemsAsync(long refundId, CancellationToken ct = default);

    /// <summary>取某订单**已退款**的明细（用于算可退余额）。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已退款明细列表。</returns>
    /// <remarks>
    /// 只统计**已退款（20）**的：待审批的退款单还没生效，
    /// 把它算进「已退」会让可退余额被提前吃掉，出现「申请时被拒之后钱却退不了了」。
    /// </remarks>
    Task<IReadOnlyList<RefundOrderItem>> ListRefundedItemsAsync(string orderNo,
        CancellationToken ct = default);

    /// <summary>分页查退款单。</summary>
    /// <param name="status">状态过滤，0 表示不限。</param>
    /// <param name="orderNo">订单号过滤，空表示不限。</param>
    /// <param name="page">页码，从 1 起。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页结果。</returns>
    Task<PagedRefunds> PageAsync(int status, string orderNo, int page, int pageSize,
        CancellationToken ct = default);

    /// <summary>条件更新退款单审批结果。</summary>
    /// <param name="refundId">退款单 Id。</param>
    /// <param name="expectedStatus">当前状态必须为 10 待审批。</param>
    /// <param name="newStatus">新状态：20 已退款 / 90 已拒绝。</param>
    /// <param name="approverId">审批人 Id。</param>
    /// <param name="approverName">审批人姓名。</param>
    /// <param name="rejectReason">拒绝原因，通过时为空。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>影响行数。返回 0 表示已被别人处理过。</returns>
    Task<int> TryApproveAsync(long refundId, int expectedStatus, int newStatus,
        long approverId, string approverName, string rejectReason, CancellationToken ct = default);
}

/// <summary>退款单分页结果。</summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record PagedRefunds(
    IReadOnlyList<RefundOrder> Items, long Total, int Page, int PageSize);
