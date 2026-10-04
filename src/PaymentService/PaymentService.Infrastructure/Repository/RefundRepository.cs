using Collaboration.Domain.Infrastructure;
using Collaboration.Domain.Repository;
using Collaboration.Domain.Context;
using Collaboration.Domain.Entities;
using FreeSql;
using PaymentService.Domain.Entities;
using PaymentService.Domain.IRepository;

namespace PaymentService.Infrastructure.Repository;

/// <summary>退款仓储实现。</summary>
public sealed class RefundRepository : CrudRepository<RefundOrder>, IRefundRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public RefundRepository(IFreeSql freeSql) : base(freeSql) { }

    /// <inheritdoc />
    public async Task<long> InsertAsync(
        RefundOrder refund,
        IReadOnlyCollection<RefundOrderItem> items,
        CancellationToken ct = default)
    {
        await Task.Run(() => Db.Transaction(() =>
        {
            if (refund.Id == 0) refund.Id = SnowflakeId.NewId();
            refund.CreatedAt = DateTime.UtcNow;
            ApplyCreator(refund);
            Db.Insert(refund).ExecuteAffrows();

            foreach (var item in items)
            {
                item.Id = SnowflakeId.NewId();
                item.CreatedAt = DateTime.UtcNow;
                item.RefundId = refund.Id;
                ApplyCreator(item);
            }

            if (items.Count > 0) Db.Insert(items.ToList()).ExecuteAffrows();
        }), ct).ConfigureAwait(false);

        return refund.Id;
    }

    /// <summary>写入创建人快照。后台实体的操作人来自租户上下文。</summary>
    /// <param name="entity">待写入实体。</param>
    private static void ApplyCreator(AdminEntityBase entity)
    {
        var ctx = TenantContextHolder.Current;
        entity.CreatedById = ctx.UserId;
        entity.CreatedByName = ctx.UserName;
        entity.OperationId = ctx.UserId;
        entity.OperationName = ctx.UserName;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RefundOrderItem>> ListItemsAsync(long refundId, CancellationToken ct = default)
        => await Db.Select<RefundOrderItem>()
            .Where(a => a.RefundId == refundId)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RefundOrderItem>> ListRefundedItemsAsync(
        string orderNo, bool includePending = false, CancellationToken ct = default)
    {
        // 「已拒绝（90）」一律不算：拒绝无副作用，额度应该还回给客户
        int[] statuses = includePending
            ? [RefundStatuses.PendingApproval, RefundStatuses.Refunded]
            : [RefundStatuses.Refunded];

        var refundIds = await Db.Select<RefundOrder>()
            .Where(a => a.OrderNo == orderNo && statuses.Contains(a.Status))
            .ToListAsync(a => a.Id, ct)
            .ConfigureAwait(false);

        if (refundIds.Count == 0) return [];

        return await Db.Select<RefundOrderItem>()
            .Where(a => refundIds.Contains(a.RefundId))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<PagedRefunds> PageAsync(int status, string orderNo, int page, int pageSize,
        CancellationToken ct = default)
    {
        var query = Db.Select<RefundOrder>();
        if (status > 0) query = query.Where(a => a.Status == status);
        if (!string.IsNullOrWhiteSpace(orderNo))
        {
            var no = orderNo.Trim();
            query = query.Where(a => a.OrderNo == no);
        }

        var total = await query.CountAsync(ct);
        if (total == 0) return new PagedRefunds([], 0, page, pageSize);

        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .OrderByDescending(a => a.Id)
            .Page(page, pageSize)
            .ToListAsync(ct);

        return new PagedRefunds(items, total, page, pageSize);
    }

    /// <inheritdoc />
    public async Task<int> TryApproveAsync(long refundId, int expectedStatus, int newStatus,
        long approverId, string approverName, string rejectReason, CancellationToken ct = default)
        => await Db.Update<RefundOrder>()
            .Where(a => a.Id == refundId && a.Status == expectedStatus)
            .Set(a => new RefundOrder
            {
                Status = newStatus,
                ApproverId = approverId,
                ApproverName = approverName,
                RejectReason = newStatus == RefundStatuses.Rejected ? rejectReason : string.Empty,
                ApprovedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public async Task<decimal> SumApprovedAmountAsync(
        DateTime from, DateTime to, long merchantId, long platformId,
        CancellationToken ct = default)
        => await Db.Select<RefundOrder>()
            .Where(a => a.Status == RefundStatuses.Refunded)
            .Where(a => a.ApprovedAt != null && a.ApprovedAt >= from && a.ApprovedAt < to)
            .Where(a => merchantId <= 0 || a.MerchantId == merchantId)
            .Where(a => platformId <= 0 || a.PlatformId == platformId)
            .SumAsync(a => a.Amount)
            .ConfigureAwait(false);
}
