using Collaboration.Domain.Infrastructure;
using Collaboration.Domain.Repository;
using FreeSql;
using PaymentService.Domain.Entities;
using PaymentService.Domain.IRepository;

namespace PaymentService.Infrastructure.Repository;

/// <summary>支付仓储实现。</summary>
public sealed class PaymentRepository : CrudRepository<PaymentOrder>, IPaymentRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public PaymentRepository(IFreeSql freeSql) : base(freeSql) { }

    /// <inheritdoc />
    public async Task<PaymentOrder?> GetByOrderNoAsync(string orderNo, CancellationToken ct = default)
        => await Db.Select<PaymentOrder>().Where(a => a.OrderNo == orderNo).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<PaymentOrder?> GetByPaymentNoAsync(string paymentNo, CancellationToken ct = default)
        => await Db.Select<PaymentOrder>().Where(a => a.PaymentNo == paymentNo).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<long> InsertOrGetAsync(PaymentOrder payment, CancellationToken ct = default)
    {
        try
        {
            return await base.InsertAsync(payment, ct);
        }
        catch (Exception ex) when (PostgresErrors.IsUniqueViolationOn(ex, "uk_payment_biz"))
        {
            // 用户反复点「去支付」是常态。命中幂等键时**返回已存在的那张单**，
            // 而不是报「重复提交」——让用户换一个金额或等超时都是坏体验
            var existing = await GetByOrderNoAsync(payment.OrderNo, ct);
            return existing?.Id ?? 0;
        }
    }

    /// <inheritdoc />
    public async Task<int> TryChangeStatusAsync(long paymentId, int expectedStatus, int newStatus,
        DateTime? paidAt, string failReason, CancellationToken ct = default)
        => await Db.Update<PaymentOrder>()
            .Where(a => a.Id == paymentId && a.Status == expectedStatus)
            .Set(a => new PaymentOrder
            {
                Status = newStatus,
                PaidAt = paidAt,
                FailReason = failReason ?? string.Empty,
                ClosedAt = newStatus == PaymentStatuses.Closed ? DateTime.UtcNow : null,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public async Task<int> CountPendingAsync(string orderNo, CancellationToken ct = default)
        => (int)await Db.Select<PaymentOrder>()
            .Where(a => a.OrderNo == orderNo && a.Status == PaymentStatuses.Pending)
            .CountAsync(ct);
}
