using Collaboration.Domain.Infrastructure;
using Collaboration.Domain.Repository;
using FreeSql;
using InventoryService.Domain.Entities;
using InventoryService.Domain.IRepository;
using Npgsql;

namespace InventoryService.Infrastructure.Repository;

/// <summary>
/// 库存仓储实现。
/// </summary>
/// <remarks>
/// 幂等靠 <b>数据库唯一索引</b>而不是「先查再插」：两个并发请求可能都查不到、然后都插，
/// 于是重复扣了两次库存。「先查再插」在并发下必然漏，唯一键才是真保证。
/// 并发靠**条件更新**（WHERE 三个计数都还是读到的值）而不是行锁：FreeSql 3.5 的 ISelect 没有暴露行锁 API，
/// 而条件更新同样是乐观并发控制，且不依赖 ORM 的方言支持。
/// 撞唯一键时识别为「这个业务号已经处理过」，返回首次记下的 after 值，不报错——
/// 重复请求是正常业务（订单重试、消息重投），不该让上游看到失败。
/// </remarks>
public sealed class StockRepository : CrudRepository<Stock>, IStockRepository
{
    private readonly IFreeSql _db;

    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public StockRepository(IFreeSql freeSql) : base(freeSql)
    {
        _db = freeSql;
    }

    /// <inheritdoc />
    public async Task<StockApplyOutcome> ApplyAsync(
        StockOperation operation,
        long operationId,
        string operationName,
        CancellationToken ct = default)
    {
        // 数量是<b>带符号</b>的：只有 adjust（后台手工调整）可以是负数，
        // 其余动作（锁定 / 扣减 / 释放 / 回补）必须为正 —— 方向由动作本身决定，
        // 不允许用负数量表达「反向操作」，否则同一个流水会同时表达两件事。
        if (operation.Quantity == 0)
        {
            return new StockApplyOutcome(false, false, 0, 0, 0, "库存数量必须为正数", StockApplyFailure.InvalidOperation);
        }

        var deltas = ResolveDeltas(operation.Action, operation.Quantity);
        if (deltas is null || (operation.Action != StockActions.Adjust && operation.Quantity < 0))
        {
            return new StockApplyOutcome(false, false, 0, 0, 0, $"未知的库存动作: {operation.Action}", StockApplyFailure.InvalidOperation);
        }

        // Db.Transaction 的委托返回 void，结果只能在外面接
        var outcome = new StockApplyOutcome(false, false, 0, 0, 0, "未执行", StockApplyFailure.None);

        try
        {
            await Task.Run(() => _db.Transaction(() =>
            {
                var stock = _db.Select<Stock>().Where(a => a.SkuId == operation.SkuId).First();
                if (stock is null)
                {
                    outcome = new StockApplyOutcome(false, false, 0, 0, 0, "库存记录不存在，请先初始化", StockApplyFailure.NotInitialized);
                    return;
                }

                var existing = _db.Select<StockFlow>()
                    .Where(a => a.BizNo == operation.BizNo
                                && a.SkuId == operation.SkuId
                                && a.Action == operation.Action)
                    .First();

                if (existing is not null)
                {
                    outcome = new StockApplyOutcome(
                        true, true, existing.AfterAvailable, existing.AfterLocked, existing.AfterDeducted);
                    return;
                }

                var newAvailable = stock.Available + deltas.Value.Available;
                var newLocked = stock.Locked + deltas.Value.Locked;
                var newDeducted = stock.Deducted + deltas.Value.Deducted;

                if (newAvailable < 0 || newLocked < 0 || newDeducted < 0)
                {
                    var reason = newAvailable < 0
                        ? $"可用库存不足（现有 {stock.Available}，需要 {operation.Quantity}）"
                        : newLocked < 0
                            ? $"锁定库存不足（现有 {stock.Locked}，需要 {operation.Quantity}）"
                            : $"已扣减库存不足（现有 {stock.Deducted}，需要 {operation.Quantity}）";

                    outcome = new StockApplyOutcome(false, false, stock.Available, stock.Locked, stock.Deducted, reason, StockApplyFailure.Shortage);
                    return;
                }

                var now = DateTime.UtcNow;

                var affected = _db.Update<Stock>()
                    .Where(a => a.Id == stock.Id
                                && a.Available == stock.Available
                                && a.Locked == stock.Locked
                                && a.Deducted == stock.Deducted)
                    .Set(a => new Stock
                    {
                        Available = newAvailable,
                        Locked = newLocked,
                        Deducted = newDeducted,
                        UpdatedAt = now
                    })
                    .ExecuteAffrows();

                if (affected == 0)
                {
                    throw new InvalidOperationException(
                        $"SKU {operation.SkuId} 的库存在本次事务期间被其它请求改动，条件更新未命中，事务回滚。");
                }

                _db.Insert(new StockFlow
                {
                    Id = SnowflakeId.NewId(),
                    CreatedAt = now,
                    BizNo = operation.BizNo,
                    SkuId = operation.SkuId,
                    Action = operation.Action,
                    Quantity = operation.Quantity,
                    BeforeAvailable = stock.Available,
                    AfterAvailable = newAvailable,
                    BeforeLocked = stock.Locked,
                    AfterLocked = newLocked,
                    BeforeDeducted = stock.Deducted,
                    AfterDeducted = newDeducted,
                    Remark = operation.Remark ?? string.Empty,
                    OperationId = operationId,
                    OperationName = operationName,
                    PlatformId = operation.PlatformId,
                    MerchantId = operation.MerchantId
                }).ExecuteAffrows();

                outcome = new StockApplyOutcome(true, false, newAvailable, newLocked, newDeducted);
            }), ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            var flow = await _db.Select<StockFlow>()
                .Where(a => a.BizNo == operation.BizNo
                            && a.SkuId == operation.SkuId
                            && a.Action == operation.Action)
                .FirstAsync(ct);

            if (flow is null) throw;

            return new StockApplyOutcome(true, true, flow.AfterAvailable, flow.AfterLocked, flow.AfterDeducted);
        }

        return outcome;
    }
    /// <inheritdoc />
    public async Task<StockApplyOutcome> InitAsync(
        StockOperation operation,
        string productName,
        string skuSpecText,
        int warnThreshold,
        long operationId,
        string operationName,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var existing = await _db.Select<Stock>().Where(a => a.SkuId == operation.SkuId).FirstAsync(ct);
        if (existing is not null)
        {
            // 已经初始化过：补上冗余的商品名 / 规格文本即可，库存数字不动
            return new StockApplyOutcome(true, true, existing.Available, existing.Locked, existing.Deducted);
        }

        var quantity = Math.Max(0, operation.Quantity);

        var stock = new Stock
        {
            Id = SnowflakeId.NewId(),
            CreatedAt = now,
            SkuId = operation.SkuId,
            ProductName = productName,
            SkuSpecText = skuSpecText,
            Available = quantity,
            WarnThreshold = Math.Max(0, warnThreshold),
            PlatformId = operation.PlatformId,
            MerchantId = operation.MerchantId,
            CreatedById = operationId,
            CreatedByName = operationName,
            OperationId = operationId,
            OperationName = operationName
        };

        _db.Insert(stock).ExecuteAffrows();

        if (!string.IsNullOrWhiteSpace(operation.BizNo))
        {
            _db.Insert(new StockFlow
            {
                Id = SnowflakeId.NewId(),
                CreatedAt = now,
                BizNo = operation.BizNo,
                SkuId = operation.SkuId,
                Action = StockActions.Init,
                Quantity = quantity,
                BeforeAvailable = 0,
                AfterAvailable = quantity,
                Remark = operation.Remark,
                OperationId = operationId,
                OperationName = operationName,
                PlatformId = operation.PlatformId,
                MerchantId = operation.MerchantId
            }).ExecuteAffrows();
        }

        return new StockApplyOutcome(true, false, quantity, 0, 0);
    }

    /// <inheritdoc />
    public async Task<Stock?> GetBySkuIdAsync(long skuId, CancellationToken ct = default)
        => await _db.Select<Stock>().Where(a => a.SkuId == skuId).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<List<Stock>> GetBySkuIdsAsync(IReadOnlyCollection<long> skuIds, CancellationToken ct = default)
    {
        if (skuIds.Count == 0) return new List<Stock>();
        var ids = skuIds.ToArray();
        return await _db.Select<Stock>().Where(a => ids.Contains(a.SkuId)).ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<(List<Stock> Items, long Total)> QueryPagedAsync(
        int page, int pageSize, string keyword, bool lowStockOnly, CancellationToken ct = default)
    {
        var select = _db.Select<Stock>();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            select = select.Where(a => a.ProductName.Contains(kw) || a.SkuSpecText.Contains(kw));
        }

        // 预警是「阈值大于 0 且低于阈值」，阈值没配的 SKU 不算预警
        if (lowStockOnly) select = select.Where(a => a.WarnThreshold > 0 && a.Available < a.WarnThreshold);

        var total = await select.CountAsync(ct);
        var items = await select.OrderByDescending(a => a.UpdatedAt).OrderByDescending(a => a.Id)
            .Page(page, pageSize).ToListAsync(ct);

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<long> CountLowStockAsync(
        long merchantId, long platformId, CancellationToken ct = default)
        => await Db.Select<Stock>()
            .Where(a => a.WarnThreshold > 0 && a.Available < a.WarnThreshold)
            .Where(a => merchantId <= 0 || a.MerchantId == merchantId)
            .Where(a => platformId <= 0 || a.PlatformId == platformId)
            .CountAsync(ct);

    /// <inheritdoc />
    public async Task<List<StockFlow>> GetFlowsAsync(long skuId, int limit, CancellationToken ct = default)
        => await _db.Select<StockFlow>()
            .Where(a => a.SkuId == skuId)
            .OrderByDescending(a => a.CreatedAt)
            .OrderByDescending(a => a.Id)
            .Limit(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<List<OrphanLockCandidate>> GetOrphanLockCandidatesAsync(
        DateTime olderThanUtc, int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 1000);

        // 第一步：取所有「早于阈值」的锁定流水
        var locks = await _db.Select<StockFlow>()
            .Where(a => a.Action == StockActions.Lock && a.CreatedAt < olderThanUtc)
            .OrderBy(a => a.CreatedAt)
            .OrderBy(a => a.Id)
            .Limit(take * 2)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (locks.Count == 0) return [];

        // 第二步：这些 bizNo 里，哪些已经被释放 / 扣减结算过了。
        // 用「不存在后续结算流水」判断，而不是逐个算差值：
        // 前者一条 IN 就能问完，且不受「释放数量与锁定数量是否相等」的脏数据影响
        var bizNos = locks.Select(a => a.BizNo).Distinct().ToList();
        var settled = await _db.Select<StockFlow>()
            .Where(a => bizNos.Contains(a.BizNo)
                && (a.Action == StockActions.Release || a.Action == StockActions.Deduct))
            .ToListAsync(a => a.BizNo, ct)
            .ConfigureAwait(false);

        var settledSet = settled.ToHashSet(StringComparer.Ordinal);
        var result = new List<OrphanLockCandidate>(take);

        foreach (var flow in locks)
        {
            if (result.Count >= take) break;
            if (settledSet.Contains(flow.BizNo)) continue;
            result.Add(new OrphanLockCandidate(flow.BizNo, flow.SkuId, flow.Quantity, flow.CreatedAt));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<Dictionary<long, int>> GetLockedMapAsync(IReadOnlyCollection<long> skuIds, CancellationToken ct = default)
    {
        if (skuIds.Count == 0) return new Dictionary<long, int>();

        var ids = skuIds.ToArray();
        var rows = await _db.Select<Stock>()
            .Where(a => ids.Contains(a.SkuId))
            .ToListAsync(ct);

        return rows.ToDictionary(a => a.SkuId, a => a.Locked);
    }

    /// <inheritdoc />
    public async Task<int> AddPendingReleaseAsync(PendingStockRelease pending, CancellationToken ct = default)
    {
        pending.Id = SnowflakeId.NewId();
        pending.CreatedAt = DateTime.UtcNow;
        return await _db.Insert(pending).ExecuteAffrowsAsync(ct);
    }

    /// <inheritdoc />
    public async Task<List<PendingStockRelease>> GetDuePendingReleasesAsync(DateTime nowUtc, int limit, CancellationToken ct = default)
        => await _db.Select<PendingStockRelease>()
            .Where(a => a.Status == PendingReleaseStatus.Pending && a.NextRetryAt <= nowUtc)
            .OrderBy(a => a.NextRetryAt)
            .OrderBy(a => a.Id)
            .Limit(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<int> UpdatePendingReleaseAsync(PendingStockRelease pending, CancellationToken ct = default)
        => await _db.Update<PendingStockRelease>()
            .Where(a => a.Id == pending.Id)
            .Set(a => new PendingStockRelease
            {
                Status = pending.Status,
                RetryCount = pending.RetryCount,
                LastError = pending.LastError,
                NextRetryAt = pending.NextRetryAt,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);

    /// <summary>把动作翻译成三个计数的增减量。</summary>
    /// <param name="action">动作。</param>
    /// <param name="quantity">数量。</param>
    /// <returns>增减量；未知动作返回 null。</returns>
    /// <remarks>
    /// 语义严格照 BUSINESS.md 9.2：
    /// 锁定 locked+q / available-q；扣减 locked-q / deducted+q；
    /// 释放 locked-q / available+q；回补 deducted-q / available+q。
    /// </remarks>
    internal static (int Available, int Locked, int Deducted)? ResolveDeltas(string action, int quantity) => action switch
    {
        StockActions.Lock => (-quantity, +quantity, 0),
        StockActions.Deduct => (0, -quantity, +quantity),
        StockActions.Release => (+quantity, -quantity, 0),
        StockActions.Replenish => (+quantity, 0, -quantity),
        StockActions.Adjust => (quantity, 0, 0),
        StockActions.SeckillReserve => (-quantity, 0, 0),
        StockActions.SeckillRelease => (+quantity, 0, 0),
        _ => null
    };
}
