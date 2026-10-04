using EvaluateService.Domain.Entities;
using EvaluateService.Domain.Exceptions;
using EvaluateService.Domain.IRepository;
using EvaluateService.Domain.Services;
using Collaboration.Domain.Context;
using Collaboration.Domain.Infrastructure;
using Collaboration.Domain.Repository;
using FreeSql;

namespace EvaluateService.Infrastructure.Repository;

/// <summary>评价仓储实现。</summary>
public sealed class EvaluateRepository : CrudRepository<Evaluate>, IEvaluateRepository
{
    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public EvaluateRepository(IFreeSql freeSql) : base(freeSql) { }

    // 🔴 这里**不重写** GetByIdAsync：基类 CrudRepository 的实现与曾经的本类实现逐字相同
    //（都是 Db.Select<Evaluate>().Where(a => a.Id == id).FirstAsync(ct)）。
    // 保留一份一模一样的重写只会让编译器报 CS0108，并且给人一个错觉：
    // 「评价的按 Id 查询有特殊逻辑」。接口照样由基类方法满足。

    /// <inheritdoc />
    public async Task<EvaluateWithRefs?> GetWithRefsAsync(long evaluateId, CancellationToken ct = default)
    {
        var evaluate = await GetByIdAsync(evaluateId, ct).ConfigureAwait(false);
        if (evaluate is null) return null;

        var refs = await Db.Select<EvaluateSkuRef>()
            .Where(a => a.EvaluateId == evaluateId)
            .OrderBy(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        return new EvaluateWithRefs(evaluate, refs);
    }

    /// <inheritdoc />
    public async Task<long> InsertWithRefsAsync(
        Evaluate evaluate,
        IReadOnlyCollection<EvaluateSkuRef> skuRefs,
        CancellationToken ct = default)
    {
        var ctx = TenantContextHolder.Current;

        // 审计字段在这里显式填，不用 AOP 钩子：FreeSql 3.5 的 CurdBefore 在本项目
        // 调用链上没触发，依赖它会写成 Id=0 / CreatedAt=0001-01-01（见 CrudRepository 注释）
        if (evaluate.Id == 0) evaluate.Id = SnowflakeId.NewId();
        evaluate.CreatedAt = DateTime.UtcNow;

        // 🔴 只在**没给**的时候才从上下文补，**不要覆盖**命令里已带的 CustomerId。
        // 本项目的接口约定是「客户端在请求体里传 CustomerId」（见 OrderController / CartController），
        // 无条件覆盖成 ctx.UserId 会得到一个更糟的结果：服务被直接调用（不经网关、
        // 没有租户上下文）时 UserId = 0，评价会被记到「客户 0」名下，
        // 于是「我的评价」查不到、追评被拒（归属校验不通过），而接口全部返回成功。
        if (evaluate.CustomerId <= 0)
        {
            evaluate.CustomerId = ctx.UserId;
            evaluate.CustomerName = ctx.UserName;
        }

        try
        {
            // FreeSql 3.5 没有 TransactionAsync，用项目统一的写法：
            // Task.Run 包一层同步 Transaction。两条写入必须在同一事务里——
            // 只写了首评没写 SKU 标记的话，这条评价在「按规格过滤」时会凭空消失
            await Task.Run(() => Db.Transaction(() =>
            {
                Db.Insert(evaluate).ExecuteAffrows();

                if (skuRefs.Count == 0) return;

                foreach (var r in skuRefs)
                {
                    r.Id = SnowflakeId.NewId();
                    r.CreatedAt = DateTime.UtcNow;
                    r.EvaluateId = evaluate.Id;
                }

                Db.Insert(skuRefs.ToList()).ExecuteAffrows();
            }), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (PostgresErrors.IsUniqueViolationOn(ex, "uk_evaluate_order_spu"))
        {
            // 撞唯一索引 = 该订单已评过这个 SPU。不用「先查再插」：
            // 两个并发提交都可能查到「还没评过」，靠数据库才拦得住。
            throw new DuplicateEvaluateException(evaluate.OrderNo, evaluate.SpuId);
        }

        return evaluate.Id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<long>> GetEvaluatedSpuIdsAsync(string orderNo, CancellationToken ct = default)
    {
        var list = await Db.Select<Evaluate>()
            .Where(a => a.OrderNo == orderNo)
            .ToListAsync(a => a.SpuId, ct).ConfigureAwait(false);

        return list.ToHashSet();
    }

    /// <inheritdoc />
    public async Task<Evaluate?> GetByOrderAndSpuAsync(string orderNo, long spuId, CancellationToken ct = default)
        => await Db.Select<Evaluate>()
            .Where(a => a.OrderNo == orderNo && a.SpuId == spuId)
            .FirstAsync(ct);

    /// <inheritdoc />
    public async Task<PagedEvaluates> PageBySpuAsync(long spuId, long skuId, int page, int pageSize,
        CancellationToken ct = default)
    {
        // 按 SKU 过滤时必须先从 sku_ref 拿评价 Id 集合，不能直接在 evaluate 上 join：
        // 「只展示标记了该 SKU 的评价」是本表存在的全部理由（规格 14.1 详情页过滤）
        var query = Db.Select<Evaluate>()
            .Where(a => a.SpuId == spuId && a.IsHidden == false);

        if (skuId > 0)
        {
            var ids = await Db.Select<EvaluateSkuRef>()
                .Where(a => a.SkuId == skuId)
                .ToListAsync(a => a.EvaluateId, ct).ConfigureAwait(false);

            query = query.Where(a => ids.Contains(a.Id));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        if (total == 0) return new PagedEvaluates([], 0, page, pageSize);

        // 显式 ORDER BY：PostgreSQL 无排序时按堆物理顺序返回，更新后会「随机排序」
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .OrderByDescending(a => a.Id)
            .Page(page, pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        return new PagedEvaluates(items, total, page, pageSize);
    }

    /// <inheritdoc />
    public async Task<PagedEvaluates> PageByCustomerAsync(long customerId, int page, int pageSize,
        CancellationToken ct = default)
    {
        // 「我的评价」**要包含已隐藏的**：客户得能看到「我评了但被隐藏了」，
        // 否则他只会以为评价丢了而反复提交。
        var query = Db.Select<Evaluate>().Where(a => a.CustomerId == customerId);
        var total = await query.CountAsync(ct).ConfigureAwait(false);

        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .OrderByDescending(a => a.Id)
            .Page(page, pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        return new PagedEvaluates(items, total, page, pageSize);
    }

    /// <inheritdoc />
    public async Task<PagedEvaluates> PageForAdminAsync(EvaluateAdminFilter filter, CancellationToken ct = default)
    {
        var query = Db.Select<Evaluate>();

        if (filter.SpuId > 0) query = query.Where(a => a.SpuId == filter.SpuId);
        if (filter.MerchantId > 0) query = query.Where(a => a.MerchantId == filter.MerchantId);
        if (filter.StarScore > 0) query = query.Where(a => a.StarScore == filter.StarScore);
        if (filter.OnlyHidden) query = query.Where(a => a.IsHidden);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .OrderByDescending(a => a.Id)
            .Page(filter.Page, filter.PageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        return new PagedEvaluates(items, total, filter.Page, filter.PageSize);
    }

    /// <inheritdoc />
    public async Task<bool> SetHiddenAsync(long evaluateId, bool isHidden, string reason, long operatorId,
        CancellationToken ct = default)
    {
        var affected = await Db.Update<Evaluate>()
            .Where(a => a.Id == evaluateId)
            .Set(a => new Evaluate
            {
                IsHidden = isHidden,
                HiddenReason = isHidden ? reason : string.Empty,
                HiddenAt = isHidden ? DateTime.UtcNow : null,
                HiddenById = isHidden ? operatorId : 0L,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvaluateAppend>> GetAppendsAsync(long evaluateId, CancellationToken ct = default)
        => await Db.Select<EvaluateAppend>()
            .Where(a => a.EvaluateId == evaluateId)
            .OrderBy(a => a.CreatedAt)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<int> CountAppendsAsync(long evaluateId, CancellationToken ct = default)
        => (int)await Db.Select<EvaluateAppend>()
            .Where(a => a.EvaluateId == evaluateId)
            .CountAsync(ct);

    /// <inheritdoc />
    public async Task<long> InsertAppendAsync(EvaluateAppend append, CancellationToken ct = default)
    {
        var ctx = TenantContextHolder.Current;

        append.Id = SnowflakeId.NewId();
        append.CreatedAt = DateTime.UtcNow;

        // 同上：只在没给时补，绝不覆盖命令里带的 CustomerId
        if (append.CustomerId <= 0)
        {
            append.CustomerId = ctx.UserId;
            append.CustomerName = ctx.UserName;
        }

        await Db.Insert(append).ExecuteAffrowsAsync(ct).ConfigureAwait(false);
        return append.Id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvaluateReply>> GetRepliesAsync(long evaluateId,
        CancellationToken ct = default)
        => await Db.Select<EvaluateReply>()
            .Where(a => a.EvaluateId == evaluateId)
            .OrderBy(a => a.CreatedAt)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, List<EvaluateSkuRef>>> GetSkuRefsBatchAsync(
        IReadOnlyCollection<long> evaluateIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, List<EvaluateSkuRef>>();
        if (evaluateIds.Count == 0) return result;

        foreach (var id in evaluateIds) result[id] = [];

        var rows = await Db.Select<EvaluateSkuRef>()
            .Where(a => evaluateIds.Contains(a.EvaluateId))
            .OrderBy(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var row in rows)
        {
            if (result.TryGetValue(row.EvaluateId, out var list)) list.Add(row);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, List<EvaluateAppend>>> GetAppendsBatchAsync(
        IReadOnlyCollection<long> evaluateIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, List<EvaluateAppend>>();
        if (evaluateIds.Count == 0) return result;

        foreach (var id in evaluateIds) result[id] = [];

        var rows = await Db.Select<EvaluateAppend>()
            .Where(a => evaluateIds.Contains(a.EvaluateId))
            .OrderBy(a => a.CreatedAt)
            .OrderBy(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var row in rows)
        {
            if (result.TryGetValue(row.EvaluateId, out var list)) list.Add(row);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, List<EvaluateReply>>> GetRepliesBatchAsync(
        IReadOnlyCollection<long> evaluateIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, List<EvaluateReply>>();
        if (evaluateIds.Count == 0) return result;

        foreach (var id in evaluateIds) result[id] = [];

        var rows = await Db.Select<EvaluateReply>()
            .Where(a => evaluateIds.Contains(a.EvaluateId))
            .OrderBy(a => a.CreatedAt)
            .OrderBy(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var row in rows)
        {
            if (result.TryGetValue(row.EvaluateId, out var list)) list.Add(row);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<long> InsertReplyAsync(EvaluateReply reply, CancellationToken ct = default)
    {
        reply.Id = SnowflakeId.NewId();
        reply.CreatedAt = DateTime.UtcNow;

        try
        {
            await Db.Insert(reply).ExecuteAffrowsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (PostgresErrors.IsUniqueViolationOn(ex, "uk_evaluate_reply_once"))
        {
            // 「每个主体对同一条评价只能回复 1 次」的唯一索引兜底。
            // 只靠「先查有没有回过」在并发下必然漏：两个请求都查到「还没回过」。
            throw new DuplicateReplyException(reply.ReplyType);
        }

        return reply.Id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, SpuRating>> AggregateBySpuAsync(
        IReadOnlyCollection<long> spuIds, CancellationToken ct = default)
    {
        if (spuIds.Count == 0) return new Dictionary<long, SpuRating>();

        // 隐去评价不计入均分：违规内容已经下架，再让它影响评分没有意义
        var rows = await Db.Select<Evaluate>()
            .Where(a => spuIds.Contains(a.SpuId) && a.IsHidden == false)
            .ToListAsync(a => new { a.SpuId, a.StarScore }, ct).ConfigureAwait(false);

        var grouped = rows.GroupBy(a => a.SpuId);
        var result = new Dictionary<long, SpuRating>(grouped.Count());

        foreach (var group in grouped)
        {
            var stars = new List<int>(group.Count());
            foreach (var row in group) stars.Add(row.StarScore);

            result[group.Key] = new SpuRating(group.Key, EvaluateCalculator.AverageScore(stars), group.Count());
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, long>> GetAllRatedSpuOwnersAsync(
        CancellationToken ct = default)
    {
        var rows = await Db.Select<Evaluate>()
            .Where(a => a.IsHidden == false)
            .ToListAsync(a => new { a.SpuId, a.MerchantId }, ct).ConfigureAwait(false);

        var result = new Dictionary<long, long>();
        foreach (var row in rows)
        {
            result[row.SpuId] = row.MerchantId;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, decimal>> AggregateMerchantRatingsAsync(
        CancellationToken ct = default)
    {
        // 店铺评分 = 该商户**有评价商品**的均分的平均值。
        //
        // 🔴 这里必须按「该商户名下有评价的 SPU」来算，而不是「该商户名下所有 SPU」。
        // 把零评价商品的默认 5.0 算进去会让评分虚高：新店刷 10 个零评价商品，
        // 店铺评分直接 5.0，比认真做生意的店还高（规格 14.5「排除无评价商品」）。
        var spuOwners = await GetAllRatedSpuOwnersAsync(ct).ConfigureAwait(false);
        if (spuOwners.Count == 0) return new Dictionary<long, decimal>();

        var ratings = await AggregateBySpuAsync(spuOwners.Keys.ToList(), ct).ConfigureAwait(false);

        var byMerchant = new Dictionary<long, List<decimal>>();
        foreach (var (spuId, merchantId) in spuOwners)
        {
            // AggregateBySpuAsync 会跳过零评价的 SPU（分组里根本没有它），这里同样要跳过
            if (!ratings.TryGetValue(spuId, out var rating)) continue;

            if (!byMerchant.TryGetValue(merchantId, out var list))
            {
                list = [];
                byMerchant[merchantId] = list;
            }

            list.Add(rating.AverageScore);
        }

        var result = new Dictionary<long, decimal>(byMerchant.Count);
        foreach (var (merchantId, scores) in byMerchant)
        {
            result[merchantId] = EvaluateCalculator.MerchantRating(scores);
        }

        return result;
    }
}
