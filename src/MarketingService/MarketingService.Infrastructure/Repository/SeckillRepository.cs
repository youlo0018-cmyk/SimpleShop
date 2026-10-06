using Collaboration.Domain.Context;
using Collaboration.Domain.Entities;
using Collaboration.Domain.Infrastructure;
using FreeSql;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;
using MarketingService.Domain.Services;
using Npgsql;

namespace MarketingService.Infrastructure.Repository;

/// <summary>秒杀仓储实现。</summary>
/// <remarks>
/// 与 <see cref="PromotionRepository"/> 一样<b>不继承</b>通用 CRUD 基类：
/// 秒杀的写操作全部需要「条件更新」语义（WHERE 里带上期望的旧状态 / 旧计数），
/// 而通用基类的 <c>SetDto</c> 不带条件，等于把并发控制整个丢掉。
/// </remarks>
public sealed class SeckillRepository : ISeckillRepository
{
    private readonly IFreeSql _db;

    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public SeckillRepository(IFreeSql freeSql) => _db = freeSql;

    /// <inheritdoc />
    public async Task<SeckillSession?> GetSessionAsync(long sessionId, CancellationToken ct = default)
        => await _db.Select<SeckillSession>().Where(a => a.Id == sessionId).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<(List<SeckillSession> Items, long Total)> ListSessionsAsync(
        int status, int page, int pageSize, CancellationToken ct = default)
    {
        var items = await _db.Select<SeckillSession>()
            .Where(a => status <= 0 || a.Status == status)
            .OrderByDescending(a => a.StartTime).OrderByDescending(a => a.Id)
            .Limit(pageSize).Offset((page - 1) * pageSize)
            .ToListAsync(ct);

        var total = await _db.Select<SeckillSession>()
            .Where(a => status <= 0 || a.Status == status)
            .CountAsync(ct);

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<List<SeckillSession>> ListPublicSessionsAsync(
        DateTime nowUtc, long platformId, CancellationToken ct = default)
        => await _db.Select<SeckillSession>()
            .Where(a => (a.Status == SeckillSessionStatuses.NotStarted || a.Status == SeckillSessionStatuses.Running)
                        && a.EndTime > nowUtc
                        && (platformId <= 0 || a.PlatformId == platformId))
            .OrderBy(a => a.StartTime).OrderBy(a => a.Id)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<List<SeckillSession>> ListExpiredRunningSessionsAsync(
        DateTime nowUtc, int limit, CancellationToken ct = default)
        => await _db.Select<SeckillSession>()
            .Where(a => a.Status == SeckillSessionStatuses.Running && a.EndTime <= nowUtc)
            // 按结束时间从早到晚：先处理「欠得最久」的，
            // 免得某一轮 limit 用光后，早该结束的场次被一直往后排。
            .OrderBy(a => a.EndTime).OrderBy(a => a.Id)
            .Limit(limit)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<List<SeckillSessionAggregate>> AggregateSessionsAsync(
        DateTime from, DateTime to, long sessionId, long merchantId, long platformId,
        int limit, CancellationToken ct = default)
    {
        var sessions = await _db.Select<SeckillSession>()
            .Where(a => a.StartTime >= from && a.StartTime < to)
            .Where(a => sessionId <= 0 || a.Id == sessionId)
            .Where(a => merchantId <= 0 || a.MerchantId == merchantId)
            .Where(a => platformId <= 0 || a.PlatformId == platformId)
            .OrderByDescending(a => a.StartTime)
            .Limit(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (sessions.Count == 0) return new List<SeckillSessionAggregate>();

        var ids = sessions.Select(a => a.Id).ToArray();

        // 一次性把商品与抢购记录捞出来在内存里分组，避免「每个场次两次查询」的 N+1。
        // 场次多的时候（几十上百个）差别很明显。
        var items = await _db.Select<SeckillItem>()
            .Where(a => ids.Contains(a.SessionId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var grabs = await _db.Select<SeckillGrab>()
            .Where(a => ids.Contains(a.SessionId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new List<SeckillSessionAggregate>(sessions.Count);

        foreach (var session in sessions)
        {
            var sessionItems = items.Where(a => a.SessionId == session.Id).ToList();

            var sessionGrabs = grabs.Where(a => a.SessionId == session.Id).ToList();

            var stockTotal = sessionItems.Sum(a => a.SeckillStock);
            var stockSold = sessionItems.Sum(a => a.SoldCount);

            // 参与人数按**去重客户**算：同一个人点 10 次也只算 1 个参与者，
            // 否则「参与人数」会被一个反复点击的用户刷上去，
            // 运营会误以为这场秒杀很抢手。
            var participants = sessionGrabs
                .Where(a => a.CustomerId > 0)
                .Select(a => a.CustomerId)
                .Distinct()
                .Count();

            // 订单号清单只取**真的落下订单**的：
            // ① 成功（1）与已退款（6）都算 —— 这两种都**真的创建了订单**，
            //    退款是订单侧的后续事件，不该把「这场抢到过几个人」抹掉。
            //    （GMV 口径由订单服务负责排除已退款，这里不掺和。）
            // ② 下单失败的抢购记录（result_status = 5）没有订单号，必须排除，
            //    混进去会让向订单服务换 GMV 时查不到单。
            var orderNos = sessionGrabs
                .Where(a =>
                    (a.ResultStatus == SeckillGrabResults.Success || a.ResultStatus == SeckillGrabResults.Refunded)
                    && !string.IsNullOrWhiteSpace(a.OrderNo))
                .Select(a => a.OrderNo)
                .Distinct()
                .ToList();

            // 显式转 decimal 再除：int / int 是整数除法，售罄率会恒为 0 或 1。
            var sellOutRate = stockTotal > 0
                ? Math.Round((decimal)stockSold / stockTotal, 4, MidpointRounding.AwayFromZero)
                : 0m;

            result.Add(new SeckillSessionAggregate(
                session.Id,
                session.SessionName,
                participants,
                orderNos.Count,
                stockTotal,
                stockSold,
                sellOutRate,
                orderNos));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<long> InsertSessionAsync(SeckillSession session, CancellationToken ct = default)
    {
        session.Id = SnowflakeId.NewId();
        session.CreatedAt = DateTime.UtcNow;
        ApplyAudit(session);
        await _db.Insert(session).ExecuteAffrowsAsync(ct);
        return session.Id;
    }

    /// <inheritdoc />
    public async Task<int> UpdateSessionAsync(SeckillSession session, CancellationToken ct = default)
    {
        var ctx = TenantContextHolder.Current;

        // 不更新 Status 与 StockTransferred：这两个只能通过各自的条件更新方法改，
        // 混进普通更新里就绕过了并发控制
        return await _db.Update<SeckillSession>()
            .Where(a => a.Id == session.Id)
            .Set(a => new SeckillSession
            {
                SessionName = session.SessionName,
                StartTime = session.StartTime,
                EndTime = session.EndTime,
                SortOrder = session.SortOrder,
                OperationId = ctx.UserId,
                OperationName = ctx.UserName,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> TrySetSessionStatusAsync(
        long sessionId, int fromStatus, int toStatus, CancellationToken ct = default)
        => await _db.Update<SeckillSession>()
            .Where(a => a.Id == sessionId && a.Status == fromStatus)
            .Set(a => new SeckillSession { Status = toStatus, UpdatedAt = DateTime.UtcNow })
            .ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public async Task<int> SetStockTransferredAsync(
        long sessionId, bool transferred, CancellationToken ct = default)
        => await _db.Update<SeckillSession>()
            .Where(a => a.Id == sessionId)
            .Set(a => new SeckillSession
            {
                StockTransferred = transferred,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public async Task<List<SeckillItem>> ListItemsAsync(long sessionId, CancellationToken ct = default)
        => await _db.Select<SeckillItem>()
            .Where(a => a.SessionId == sessionId)
            .OrderBy(a => a.SortOrder).OrderBy(a => a.Id)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<List<SeckillItem>> ListItemsBySkuAsync(long skuId, CancellationToken ct = default)
        => await _db.Select<SeckillItem>()
            .Where(a => a.SkuId == skuId)
            .OrderByDescending(a => a.Id)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<SeckillItem?> GetItemAsync(long itemId, CancellationToken ct = default)
        => await _db.Select<SeckillItem>().Where(a => a.Id == itemId).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<long> InsertItemAsync(SeckillItem item, CancellationToken ct = default)
    {
        item.Id = SnowflakeId.NewId();
        item.CreatedAt = DateTime.UtcNow;
        ApplyAudit(item);
        await _db.Insert(item).ExecuteAffrowsAsync(ct);
        return item.Id;
    }

    /// <inheritdoc />
    public async Task<int> UpdateItemAsync(SeckillItem item, CancellationToken ct = default)
    {
        var ctx = TenantContextHolder.Current;

        // 刻意**不**更新 SoldCount：已抢数量只能靠 TryIncreaseSold / TryDecreaseSold 改，
        // 那两个是条件更新，普通更新一旦带上 sold_count 就会把并发控制绕过去
        return await _db.Update<SeckillItem>()
            .Where(a => a.Id == item.Id)
            .Set(a => new SeckillItem
            {
                SeckillPrice = item.SeckillPrice,
                PerUserLimit = item.PerUserLimit,
                Status = item.Status,
                SortOrder = item.SortOrder,
                OperationId = ctx.UserId,
                OperationName = ctx.UserName,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> SoftDeleteItemAsync(long itemId, CancellationToken ct = default)
        => await _db.Update<SeckillItem>()
            .Where(a => a.Id == itemId)
            .Set(a => new SeckillItem
            {
                IsDeleted = true, DeletedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public async Task<int> TryIncreaseSoldAsync(long itemId, int quantity, CancellationToken ct = default)
    {
        // 条件里同时判三件事：商品存在、启用中、加上本次之后仍不超库存。
        // 三个条件写在同一条 UPDATE 里，PostgreSQL 会对整行加锁，
        // 所以两个并发请求里必然只有一个能命中。
        var affected = await _db.Update<SeckillItem>()
            .Where(a => a.Id == itemId
                        && a.Status == SeckillItemStatuses.Enabled
                        && a.SoldCount + quantity <= a.SeckillStock)
            .Set(a => new SeckillItem
            {
                SoldCount = a.SoldCount + quantity,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);

        return affected;
    }

    /// <inheritdoc />
    public async Task<int> TryDecreaseSoldAsync(long itemId, int quantity, CancellationToken ct = default)
    {
        // 回补也要条件更新：WHERE 里带上 sold_count >= quantity，
        // 防止两次回补把 sold_count 减成负数（那会让库存凭空多出来）
        var affected = await _db.Update<SeckillItem>()
            .Where(a => a.Id == itemId && a.SoldCount >= quantity)
            .Set(a => new SeckillItem
            {
                SoldCount = a.SoldCount - quantity,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);

        return affected;
    }

    /// <inheritdoc />
    public async Task<long> InsertGrabAsync(SeckillGrab grab, CancellationToken ct = default)
    {
        grab.Id = SnowflakeId.NewId();
        grab.CreatedAt = DateTime.UtcNow;
        var ctx = TenantContextHolder.Current;
        grab.CreatedById = ctx.UserId;
        grab.CreatedByName = ctx.UserName;
        grab.OperationId = ctx.UserId;
        grab.OperationName = ctx.UserName;

        try
        {
            await _db.Insert(grab).ExecuteAffrowsAsync(ct);
            return grab.Id;
        }
        catch (Exception ex) when (PostgresErrors.IsUniqueViolationOn(ex, "uk_seckill_grab"))
        {
            // 撞 uk_seckill_grab_biz → 同一客户重复抢同一商品。
            // 这是限购的最后防线，Redis 与应用层判断已经挡掉绝大多数。
            // 这里判的是「异常链里有没有唯一约束冲突」而不是按类型 catch：
            // FreeSql 会把 PostgresException 包进普通 Exception，
            // 直接 catch (PostgresException) 会漏掉这一分支（已真实踩过，返回 500 而不是「超出限购」）。
            throw new DuplicateGrabException(grab.BizNo);
        }
    }

    /// <inheritdoc />
    public async Task<SeckillGrab?> GetGrabByBizNoAsync(string bizNo, CancellationToken ct = default)
        => await _db.Select<SeckillGrab>().Where(a => a.BizNo == bizNo).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<SeckillGrab?> GetGrabByRequestIdAsync(string requestId, CancellationToken ct = default)
        => await _db.Select<SeckillGrab>().Where(a => a.RequestId == requestId).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<int> UpdateGrabAsync(SeckillGrab grab, CancellationToken ct = default)
        => await _db.Update<SeckillGrab>()
            .Where(a => a.Id == grab.Id)
            .Set(a => new SeckillGrab
            {
                ResultStatus = grab.ResultStatus,
                ResultMessage = grab.ResultMessage,
                OrderId = grab.OrderId,
                OrderNo = grab.OrderNo,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);

    private static void ApplyAudit(AdminEntityBase entity)
    {
        var ctx = TenantContextHolder.Current;
        entity.CreatedById = ctx.UserId;
        entity.CreatedByName = ctx.UserName;
        entity.OperationId = ctx.UserId;
        entity.OperationName = ctx.UserName;
    }
}
