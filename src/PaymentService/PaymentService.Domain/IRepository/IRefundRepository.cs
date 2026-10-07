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

    /// <summary>取某订单已占用的退款明细（用于算可退余额）。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="includePending">是否把「待审批」也算作已占用。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>退款明细列表。</returns>
    /// <remarks>
    /// <paramref name="includePending"/> 为 true 时把「待审批（10）」也算作已占用，
    /// 这样同一订单开不出两张注定过不了的退款单，用户当场就能看到「可退余额不足」。
    ///
    /// <para><b>审批时必须传 false</b>：审批要校验的是「实际已退了多少」，
    /// 把本单自己算进去就成了「它永远超自己」。</para>
    ///
    /// <para>「已拒绝（90）」一律不算——拒绝无副作用，额度应该还回给客户。</para>
    /// </remarks>
    Task<IReadOnlyList<RefundOrderItem>> ListRefundedItemsAsync(string orderNo,
        bool includePending = false, CancellationToken ct = default);

    /// <summary>分页查退款单。</summary>
    /// <param name="status">状态过滤，0 表示不限。</param>
    /// <param name="orderNo">订单号过滤，空表示不限。</param>
    /// <param name="keyword">退款单号 / 订单号模糊搜索，空表示不限。</param>
    /// <param name="page">页码，从 1 起。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页结果。</returns>
    /// <remarks>
    /// <c>Keyword</c> 是后台搜索框用的：占位符写的是「退款单号 / 订单号」，
    /// 但查询命令里原来**只有 OrderNo** —— 前端发过来的 keyword 被静默忽略，
    /// 搜索框看起来能用、其实一条都筛不掉（输退款单号也返回全部）。
    /// </remarks>
    Task<PagedRefunds> PageAsync(int status, string orderNo, string keyword, int page, int pageSize,
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

    /// <summary>按区间汇总「审批通过」的退款金额，供工作台报表使用。</summary>
    /// <param name="from">区间起（含）。</param>
    /// <param name="to">区间止（不含）。</param>
    /// <param name="merchantId">商户 Id，0 表示不限。</param>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>退款金额合计；无记录返回 0。</returns>
    /// <remarks>
    /// <b>只统计审批通过（20）且按 approved_at 落在区间内</b>的两类：
    /// 待审批的钱还没退出去，算进「退款金额」会让退款率虚高；
    /// 按申请时间统计的话，月初申请月底批的单会整个落在这个月，
    /// 而这笔钱是月底才退的——和 GMV 用支付时间是一个道理。
    /// </remarks>
    Task<decimal> SumApprovedAmountAsync(
        DateTime from, DateTime to, long merchantId, long platformId,
        CancellationToken ct = default);
}

/// <summary>退款单分页结果。</summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record PagedRefunds(
    IReadOnlyList<RefundOrder> Items, long Total, int Page, int PageSize);
