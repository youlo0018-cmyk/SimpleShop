using PaymentService.Domain.Entities;

namespace PaymentService.Domain.IRepository;

/// <summary>支付仓储。</summary>
public interface IPaymentRepository
{
    /// <summary>按订单号取支付单。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>找到返回实体，否则 null。</returns>
    Task<PaymentOrder?> GetByOrderNoAsync(string orderNo, CancellationToken ct = default);

    /// <summary>按支付单号取支付单。</summary>
    /// <param name="paymentNo">支付单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>找到返回实体，否则 null。</returns>
    Task<PaymentOrder?> GetByPaymentNoAsync(string paymentNo, CancellationToken ct = default);

    /// <summary>插入支付单（幂等键 {order_no}:{channel}）。</summary>
    /// <param name="payment">支付单。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>支付单 Id。</returns>
    /// <remarks>命中幂等键时**返回已存在的单**而不是报错——用户反复点「去支付」是常态。</remarks>
    Task<long> InsertOrGetAsync(PaymentOrder payment, CancellationToken ct = default);

    /// <summary>条件更新支付状态。</summary>
    /// <param name="paymentId">支付单 Id。</param>
    /// <param name="expectedStatus">当前状态必须等于它（乐观条件）。</param>
    /// <param name="newStatus">新状态。</param>
    /// <param name="paidAt">支付成功时间。</param>
    /// <param name="failReason">失败原因。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>影响行数。返回 0 表示状态已被别人改过。</returns>
    /// <remarks>
    /// 带上「期望状态」是支付幂等的关键：重复确认时第二次拿到 0，
    /// 于是<b>不会重复触发</b>「订单改已支付 + 发积分 + 发券」这些副作用。
    /// </remarks>
    Task<int> TryChangeStatusAsync(long paymentId, int expectedStatus, int newStatus,
        DateTime? paidAt, string failReason, CancellationToken ct = default);

    /// <summary>统计某订单的待支付单数量。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>数量。</returns>
    Task<int> CountPendingAsync(string orderNo, CancellationToken ct = default);

    /// <summary>后台分页查支付单。</summary>
    /// <param name="status">状态过滤，0 表示不限。</param>
    /// <param name="keyword">按支付单号 / 订单号模糊匹配。</param>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="merchantId">商户 Id，0 表示不限。</param>
    /// <param name="page">页码，从 1 起。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当前页数据与总条数。</returns>
    /// <remarks>
    /// 之前支付单**只能按订单号查单条**（<c>GetByOrderNoAsync</c>），后台没有任何分页入口 ——
    /// 运营想看「今天有哪些单支付失败」都做不到。
    /// </remarks>
    Task<PagedPayments> PageAsync(
        int status, string keyword, long platformId, long merchantId,
        int page, int pageSize, CancellationToken ct = default);
}

/// <summary>支付单分页结果。</summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record PagedPayments(
    IReadOnlyList<PaymentOrder> Items, long Total, int Page, int PageSize);
