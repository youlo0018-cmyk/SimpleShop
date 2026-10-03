using Collaboration.Domain.Infrastructure;
using Collaboration.Domain.Repository;
using FreeSql;
using Npgsql;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;

namespace OrderService.Infrastructure.Repository;

/// <summary>订单落单端口的 FreeSql 实现。</summary>
/// <remarks>
/// <para><b>订单主表与订单行必须在同一个事务里写</b>：只写主表不写行，
/// 订单详情页会显示成「一件商品都没有」，而金额又是按行算出来的，对账立刻对不上。</para>
///
/// <para><b>幂等靠数据库唯一索引兜底</b>（<c>uk_order_idempotency</c>），不靠「先查再插」。
/// 客户锁与应用层查询已经挡掉了绝大多数重复请求，但锁会过期、进程会崩，
/// 唯一约束是最后那道不会失效的防线。撞唯一键时<b>不当错误</b>：
/// 重新按幂等键查一次，把已存在的订单返回去，让重复请求拿到同一个订单号。</para>
///
/// <para>只有唯一索引冲突才走上面的兜底分支。其它数据库异常（连接断、字段超长、
/// 约束不满足）必须原样抛出——把它们也吞成「幂等命中」会让真实故障被伪装成成功。</para>
/// </remarks>
public sealed class OrderStore : CrudRepository<Order>, IOrderStore
{
    private readonly IFreeSql _db;

    /// <summary>构造落单端口。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public OrderStore(IFreeSql freeSql) : base(freeSql) => _db = freeSql;

    /// <inheritdoc />
    public async Task<Order?> FindByIdempotencyKeyAsync(
        long customerId, string idempotencyKey, CancellationToken ct = default)
        => await _db.Select<Order>()
            .Where(a => a.CustomerId == customerId && a.IdempotencyKey == idempotencyKey)
            .FirstAsync(ct);

    /// <inheritdoc />
    public async Task<Order?> FindByOrderNoAsync(string orderNo, CancellationToken ct = default)
        => await _db.Select<Order>().Where(a => a.OrderNo == orderNo).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<List<OrderItem>> ListItemsAsync(long orderId, CancellationToken ct = default)
        => await _db.Select<OrderItem>()
            .Where(a => a.OrderId == orderId)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<(List<Order> Orders, long Total)> ListByCustomerAsync(
        long customerId, int status, int page, int pageSize, CancellationToken ct = default)
    {
        // 把条件先固化成表达式，count 与 data 共用同一个：
        // 两处各写一遍条件，早晚有一次改漏，翻页就会出现「最后一页之后还有数据」。
        System.Linq.Expressions.Expression<Func<Order, bool>> predicate = a =>
            a.CustomerId == customerId && (status <= 0 || a.Status == status);

        var total = await _db.Select<Order>().Where(predicate).CountAsync(ct);
        var orders = await _db.Select<Order>().Where(predicate)
            .OrderByDescending(a => a.CreatedAt).OrderByDescending(a => a.Id)
            .Limit(pageSize).Offset((page - 1) * pageSize).ToListAsync(ct);

        return (orders, total);
    }

    /// <inheritdoc />
    public async Task<(List<Order> Orders, long Total)> ListAsync(
        int status, string keyword, long platformId, long merchantId,
        int page, int pageSize, CancellationToken ct = default)
    {
        var like = (keyword ?? string.Empty).Trim();

        System.Linq.Expressions.Expression<Func<Order, bool>> predicate = a =>
            (status <= 0 || a.Status == status)
            && (platformId <= 0 || a.PlatformId == platformId)
            && (merchantId <= 0 || a.MerchantId == merchantId)
            && (string.IsNullOrEmpty(like)
                || a.OrderNo.Contains(like)
                || a.ReceiverName.Contains(like)
                || a.ReceiverPhone.Contains(like));

        var total = await _db.Select<Order>().Where(predicate).CountAsync(ct);
        var orders = await _db.Select<Order>().Where(predicate)
            .OrderByDescending(a => a.CreatedAt).OrderByDescending(a => a.Id)
            .Limit(pageSize).Offset((page - 1) * pageSize).ToListAsync(ct);

        return (orders, total);
    }

    /// <inheritdoc />
    public async Task<Dictionary<long, OrderItemAggregate>> AggregateItemsAsync(
        IReadOnlyCollection<long> orderIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, OrderItemAggregate>();
        if (orderIds.Count == 0) return result;

        var ids = orderIds.Distinct().ToArray();

        // 一次把订单行全捞出来在内存里聚合：一次往返 vs 20 次往返。
        // 订单行的行数受单笔上限（50 行）约束，单页最多 50 单，
        // 最坏 2500 行，对内存不构成压力。
        var rows = await _db.Select<OrderItem>()
            .Where(a => ids.Contains(a.OrderId))
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

        foreach (var group in rows.GroupBy(a => a.OrderId))
        {
            var list = group.ToArray();
            result[group.Key] = new OrderItemAggregate(
                group.Key,
                list.Sum(a => a.Quantity),
                list.Length,
                list[0].ProductName);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<int> TryTransitStatusAsync(
        long orderId, int fromStatus, int toStatus, CancellationToken ct = default)
    {
        // 注意这里必须显式 Where + Set：FreeSql 3.5 下 Db.Update<T>(entity)
        // 在雪花主键（IsIdentity=false）上会生成空 SET，一条 SQL 都不发，
        // 返回 0 且不报错——接口回「成功」而状态纹丝不动（CRUD 基类里记了这个坑）。
        return await _db.Update<Order>()
            .Where(a => a.Id == orderId && a.Status == fromStatus)
            .Set(a => new Order { Status = toStatus, UpdatedAt = DateTime.UtcNow })
            .ExecuteAffrowsAsync(ct);
    }


    /// <inheritdoc />
    public async Task<Order> SaveAsync(Order order, IReadOnlyCollection<OrderItem> items, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // 雪花 Id 在这里显式生成。应用层客户锁 + 幂等查询已经挡住了绝大多数重复请求，
        // 这里的唯一索引是最后一道防线，所以只需处理「锁过期 / 进程崩溃」这种极端并发。
        for (var attempt = 1; ; attempt++)
        {
            order.Id = SnowflakeId.NewId();
            order.CreatedAt = now;
            order.UpdatedAt = null;

            foreach (var item in items)
            {
                item.Id = SnowflakeId.NewId();
                item.OrderId = order.Id;
                item.CreatedAt = now;
                item.UpdatedAt = null;
            }

            try
            {
                await Task.Run(() => _db.Transaction(() =>
                {
                    _db.Insert(order).ExecuteAffrows();
                    foreach (var item in items)
                    {
                        _db.Insert(item).ExecuteAffrows();
                    }
                }), ct).ConfigureAwait(false);

                return order;
            }
            catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
            {
                if (IsIdempotencyConflict(ex))
                {
                    // 同一个客户 + 同一个幂等键已经有单了。把那张单返回去，
                    // 重复提交就得到同一个订单号，而不是一个「重复下单」的报错。
                    var existing = await FindByIdempotencyKeyAsync(order.CustomerId, order.IdempotencyKey, ct)
                                   .ConfigureAwait(false);

                    if (existing is not null) return existing;
                }

                if (IsOrderNoConflict(ex) && attempt < 3)
                {
                    // 订单号撞了（同一秒内并发概率极低，但不为 0）：重新生成再试。
                    // 最多三次，再撞就抛出去——那已经不是概率问题，是有别的地方坏了。
                    order.OrderNo = NewOrderNo();
                    continue;
                }

                throw;
            }
        }
    }

    /// <summary>PostgreSQL 唯一约束冲突的 SQLSTATE。</summary>
    private const string UniqueViolation = "23505";

    /// <summary>生成新订单号。格式 yyyyMMddHHmmss + 6 位随机段。</summary>
    /// <returns>订单号。</returns>
    private static string NewOrderNo()
        => $"{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.Next(100000, 999999)}";

    /// <summary>撞的是不是幂等键那一条索引。</summary>
    /// <param name="ex">数据库异常。</param>
    /// <returns>是幂等键冲突返回 true。</returns>
    private static bool IsIdempotencyConflict(PostgresException ex)
        => (ex.ConstraintName ?? string.Empty).Contains("uk_order_idempotency", StringComparison.Ordinal);

    /// <summary>撞的是不是订单号那一条索引。</summary>
    /// <param name="ex">数据库异常。</param>
    /// <returns>是订单号冲突返回 true。</returns>
    private static bool IsOrderNoConflict(PostgresException ex)
        => (ex.ConstraintName ?? string.Empty).Contains("uk_order_no", StringComparison.Ordinal);
}