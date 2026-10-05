using Collaboration.Domain.Infrastructure;
using Collaboration.Domain.Repository;
using FreeSql;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;
using MarketingService.Domain.Services;

namespace MarketingService.Infrastructure.Repository;

/// <summary>券仓储实现。</summary>
/// <remarks>
/// 并发控制靠<b>条件更新</b>：领券要同时推进模板池子与活动池子，
/// 占券要把券从「未使用」改成「已占用」，两处都必须带上「值仍等于读到的值」这个条件。
/// 不加的话，两个并发下单可能各自算出同一张券可用，然后都占用它。
/// </remarks>
public sealed class CouponRepository : CrudRepository<UserCoupon>, ICouponRepository
{
    private readonly IFreeSql _db;

    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    public CouponRepository(IFreeSql freeSql) : base(freeSql)
    {
        _db = freeSql;
    }

    /// <inheritdoc />
    public async Task<ClaimResult> ClaimAsync(
        long customerId, long activityId, int quantity, DateTime nowUtc, CancellationToken ct = default)
    {
        if (quantity <= 0)
        {
            return new ClaimResult(
                new CouponOutcome(false, false, 0, 0m, "领取张数必须为正"), Array.Empty<string>());
        }

        var result = new CouponOutcome(false, false, 0, 0m, "未执行");
        var codes = new List<string>();

        await Task.Run(() => _db.Transaction(() =>
        {
            codes.Clear();

            var activity = _db.Select<CouponActivity>().Where(a => a.Id == activityId).First();
            if (activity is null)
            {
                result = new CouponOutcome(false, false, 0, 0m, "券活动不存在");
                return;
            }

            if (activity.Status != 1)
            {
                result = new CouponOutcome(false, false, 0, 0m, "券活动已停用");
                return;
            }

            // 时间窗：领取时间与券有效期是两个不同概念，这里判的是**领取窗口**
            if (nowUtc < activity.ClaimStartTime || nowUtc > activity.ClaimEndTime)
            {
                result = new CouponOutcome(false, false, 0, 0m, "不在领取时间内");
                return;
            }

            var template = _db.Select<CouponTemplate>().Where(a => a.Id == activity.TemplateId).First();
            if (template is null || template.Status != 1)
            {
                result = new CouponOutcome(false, false, 0, 0m, "券模板不存在或已停用");
                return;
            }

            // 活动池子：本次发放量不能超过剩余
            if (activity.ClaimQuantity - activity.ClaimedQuantity < quantity)
            {
                result = new CouponOutcome(false, false, 0, 0m,
                    $"券活动剩余不足（剩余 {activity.ClaimQuantity - activity.ClaimedQuantity} 张，需要 {quantity} 张）");
                return;
            }

            // 模板池子：TotalQuantity = 0 表示不限量
            if (template.TotalQuantity > 0 && template.TotalQuantity - template.IssuedQuantity < quantity)
            {
                result = new CouponOutcome(false, false, 0, 0m,
                    $"券模板剩余不足（剩余 {template.TotalQuantity - template.IssuedQuantity} 张，需要 {quantity} 张）");
                return;
            }

            // 每人限领：取本值与模板值的**较小者**
            var limit = Math.Min(activity.PerUserLimit, template.PerUserLimit);
            // 同步 Count()：这里在事务的同步委托里，不能用 await。限领上限是个位数，转换安全。
            var claimed = (int)_db.Select<UserCoupon>()
                .Where(a => a.CustomerId == customerId && a.ActivityId == activityId)
                .Count();
            if (claimed + quantity > limit)
            {
                result = new CouponOutcome(false, false, 0, 0m, $"已达每人限领（{limit} 张）");
                return;
            }

            // 先扣池子再发券，顺序不能反：反过来的话发完券发现池子不够，还得回滚已发的券
            var activityAffected = _db.Update<CouponActivity>()
                .Where(a => a.Id == activity.Id && a.ClaimedQuantity == activity.ClaimedQuantity)
                .Set(a => new CouponActivity { ClaimedQuantity = activity.ClaimedQuantity + quantity })
                .ExecuteAffrows();

            var templateAffected = template.TotalQuantity > 0
                ? _db.Update<CouponTemplate>()
                    .Where(a => a.Id == template.Id && a.IssuedQuantity == template.IssuedQuantity)
                    .Set(a => new CouponTemplate { IssuedQuantity = template.IssuedQuantity + quantity })
                    .ExecuteAffrows()
                : 1;

            if (activityAffected == 0 || templateAffected == 0)
            {
                throw new InvalidOperationException("券池子在本次事务期间被其它请求改动，事务回滚。");
            }

            // 有效期从**领取时刻**起算，不从活动开始时间算
            var expireAt = nowUtc.AddDays(template.ValidDays);

            for (var i = 0; i < quantity; i++)
            {
                var coupon = new UserCoupon
                {
                    Id = SnowflakeId.NewId(),
                    CreatedAt = nowUtc,
                    CustomerId = customerId,
                    TemplateId = template.Id,
                    ActivityId = activity.Id,
                    CouponCode = NewCouponCode(nowUtc, i),
                    // ↓ 快照：模板改多少次都不影响这张券
                    CouponType = template.CouponType,
                    ThresholdAmount = template.ThresholdAmount,
                    DiscountAmount = template.DiscountAmount,
                    DiscountRate = template.DiscountRate,
                    ValidDays = template.ValidDays,
                    // ↑ 快照结束
                    TargetType = activity.TargetType,
                    Targets = activity.Targets,
                    Status = CouponStatuses.Unused,
                    ExpireAt = expireAt,
                    ReceiveAt = nowUtc,
                    OrderNo = string.Empty
                };

                _db.Insert(coupon).ExecuteAffrows();
                codes.Add(coupon.CouponCode);
            }

            result = new CouponOutcome(true, false, 0, 0m);
        }), ct);

        return new ClaimResult(result, codes);
    }

    /// <inheritdoc />
    public async Task<CouponOutcome> OccupyAsync(
        long customerId, string orderNo, long couponId,
        IReadOnlyList<CouponOrderLine> lines, DateTime nowUtc, CancellationToken ct = default)
    {
        var result = new CouponOutcome(false, false, 0, 0m, "未执行");

        try
        {
            await Task.Run(() => _db.Transaction(() =>
            {
                // 同一订单只占一张。重复调用返回首次结果而不是报错——
                // 下单重试是正常业务，报错会让上游以为要重新下单。
                var existingOccupancy = _db.Select<CouponOccupancy>()
                    .Where(a => a.OrderNo == orderNo).First();

                if (existingOccupancy is not null)
                {
                    var alreadyCoupon = _db.Select<UserCoupon>().Where(a => a.Id == existingOccupancy.CouponId).First();
                    result = new CouponOutcome(true, true, existingOccupancy.CouponId, existingOccupancy.DiscountAmount);
                    return;
                }

                UserCoupon coupon;

                if (couponId > 0)
                {
                    coupon = _db.Select<UserCoupon>().Where(a => a.Id == couponId).First()
                        ?? throw new InvalidOperationException($"券 {couponId} 不存在。");
                }
                else
                {
                    // 自动选最优券
                    var available = _db.Select<UserCoupon>()
                        .Where(a => a.CustomerId == customerId
                                    && a.Status == CouponStatuses.Unused
                                    && a.ExpireAt > nowUtc)
                        .ToList();

                    if (!CouponCalculator.TryPickBest(available, lines, out var best))
                    {
                        result = new CouponOutcome(true, false, 0, 0m);
                        return;
                    }

                    coupon = available.First(a => a.Id == best!.Value.CouponId);
                }

                var quote = CouponCalculator.Quote(coupon, lines);

                if (coupon.CustomerId != customerId)
                {
                    result = new CouponOutcome(false, false, 0, 0m, "券不属于该客户");
                    return;
                }

                if (!quote.ReachedThreshold || quote.DiscountAmount <= 0m)
                {
                    result = new CouponOutcome(false, false, coupon.Id, 0m,
                        string.IsNullOrEmpty(quote.Reason) ? "该券在当前订单下不可用" : quote.Reason);
                    return;
                }

                // 条件更新：只有仍是「未使用」才能占用，防止同一张券被两个并发订单占用
                var affected = _db.Update<UserCoupon>()
                    .Where(a => a.Id == coupon.Id && a.Status == CouponStatuses.Unused)
                    .Set(a => new UserCoupon { Status = CouponStatuses.Occupied, OrderNo = orderNo, UpdatedAt = nowUtc })
                    .ExecuteAffrows();

                if (affected == 0)
                {
                    throw new InvalidOperationException(
                        $"券 {coupon.Id} 在本次事务期间已被其它订单占用，事务回滚。");
                }

                _db.Insert(new CouponOccupancy
                {
                    Id = SnowflakeId.NewId(),
                    CreatedAt = nowUtc,
                    CustomerId = customerId,
                    OrderNo = orderNo,
                    CouponId = coupon.Id,
                    DiscountAmount = quote.DiscountAmount,
                    Status = OccupancyStatuses.Occupied
                }).ExecuteAffrows();

                result = new CouponOutcome(true, false, coupon.Id, quote.DiscountAmount);
            }), ct);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            // 唯一索引挡住了重复占同一订单
            var existing = await _db.Select<CouponOccupancy>().Where(a => a.OrderNo == orderNo).FirstAsync(ct);
            if (existing is null) throw;
            return new CouponOutcome(true, true, existing.CouponId, existing.DiscountAmount);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<CouponOutcome> ConsumeAsync(long customerId, string orderNo, CancellationToken ct = default)
    {
        var result = new CouponOutcome(false, false, 0, 0m, "未执行");

        await Task.Run(() => _db.Transaction(() =>
        {
            var occupancy = _db.Select<CouponOccupancy>().Where(a => a.OrderNo == orderNo).First();
            if (occupancy is null)
            {
                result = new CouponOutcome(true, false, 0, 0m);
                return;
            }

            if (occupancy.Status == OccupancyStatuses.Consumed)
            {
                result = new CouponOutcome(true, true, occupancy.CouponId, occupancy.DiscountAmount);
                return;
            }

            if (occupancy.Status != OccupancyStatuses.Occupied)
            {
                result = new CouponOutcome(false, false, occupancy.CouponId, occupancy.DiscountAmount,
                    "该券已被回退，无法核销");
                return;
            }

            _db.Update<UserCoupon>().Where(a => a.Id == occupancy.CouponId && a.Status == CouponStatuses.Occupied)
                .Set(a => new UserCoupon { Status = CouponStatuses.Consumed, ConsumeAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow })
                .ExecuteAffrows();

            _db.Update<CouponOccupancy>().Where(a => a.Id == occupancy.Id)
                .Set(a => new CouponOccupancy { Status = OccupancyStatuses.Consumed, UpdatedAt = DateTime.UtcNow })
                .ExecuteAffrows();

            result = new CouponOutcome(true, false, occupancy.CouponId, occupancy.DiscountAmount);
        }), ct);

        return result;
    }

    /// <inheritdoc />
    public async Task<CouponOutcome> ReleaseAsync(long customerId, string orderNo, CancellationToken ct = default)
    {
        var result = new CouponOutcome(false, false, 0, 0m, "未执行");

        await Task.Run(() => _db.Transaction(() =>
        {
            var occupancy = _db.Select<CouponOccupancy>().Where(a => a.OrderNo == orderNo).First();
            if (occupancy is null)
            {
                result = new CouponOutcome(true, false, 0, 0m);
                return;
            }

            if (occupancy.Status == OccupancyStatuses.Released)
            {
                result = new CouponOutcome(true, true, occupancy.CouponId, 0m);
                return;
            }

            if (occupancy.Status == OccupancyStatuses.Consumed)
            {
                // 已核销的券不能回退：钱已经收了，券也作废了。
                // 这种情况应该走退款流程，而不是取消。
                result = new CouponOutcome(false, false, occupancy.CouponId, occupancy.DiscountAmount,
                    "该券已核销，不能回退占用");
                return;
            }

            // 券回到可用，清掉订单号
            _db.Update<UserCoupon>().Where(a => a.Id == occupancy.CouponId && a.Status == CouponStatuses.Occupied)
                .Set(a => new UserCoupon { Status = CouponStatuses.Unused, OrderNo = string.Empty, UpdatedAt = DateTime.UtcNow })
                .ExecuteAffrows();

            _db.Update<CouponOccupancy>().Where(a => a.Id == occupancy.Id)
                .Set(a => new CouponOccupancy { Status = OccupancyStatuses.Released, UpdatedAt = DateTime.UtcNow })
                .ExecuteAffrows();

            result = new CouponOutcome(true, false, occupancy.CouponId, 0m);
        }), ct);

        return result;
    }

    /// <inheritdoc />
    public async Task<List<UserCoupon>> ListAvailableAsync(long customerId, DateTime nowUtc, CancellationToken ct = default)
        => await _db.Select<UserCoupon>()
            .Where(a => a.CustomerId == customerId
                        && a.Status == CouponStatuses.Unused
                        && a.ExpireAt > nowUtc)
            .OrderBy(a => a.ExpireAt)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<UserCoupon?> GetCouponAsync(long couponId, CancellationToken ct = default)
        => await _db.Select<UserCoupon>().Where(a => a.Id == couponId).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<int> CountClaimedAsync(long customerId, long activityId, CancellationToken ct = default)
    {
        // FreeSql 的 CountAsync 返回 long，契约用 int：限领上限本来就是个位数，转换安全
        var count = await _db.Select<UserCoupon>()
            .Where(a => a.CustomerId == customerId && a.ActivityId == activityId)
            .CountAsync(ct);
        return (int)count;
    }

    /// <inheritdoc />
    public async Task<CouponTemplate?> GetTemplateAsync(long templateId, CancellationToken ct = default)
        => await _db.Select<CouponTemplate>().Where(a => a.Id == templateId).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<CouponActivity?> GetActivityAsync(long activityId, CancellationToken ct = default)
        => await _db.Select<CouponActivity>().Where(a => a.Id == activityId).FirstAsync(ct);

    /// <summary>
    /// 生成券码：日期 + 活动 + 客户 + 序号，肉眼可读且便于客服定位。
    /// </summary>
    /// <remarks>
    /// 不含随机段：同一活动同一客户同一天领的券，靠序号区分就够了；
    /// 真正的唯一性由数据库的 uk_user_coupon_code 兜底——万一重复了会报唯一键冲突，
    /// 而不是悄悄发两张一样的券。
    /// </remarks>
    private static string NewCouponCode(DateTime nowUtc, int index)
        => $"{nowUtc:yyyyMMddHHmmss}{index:D2}{Random.Shared.Next(100000, 999999)}";

    /// <inheritdoc />
    public async Task<CouponReportAggregate> AggregateAsync(
        DateTime from, DateTime to, long merchantId, long platformId,
        CancellationToken ct = default)
    {
        // 发放 = 运营配的库存总量。它是「配置值」而不是「发生额」，所以不带时间过滤：
        // 按区间截断会让「上个月配的 1000 张」在本月报表里凭空少掉。
        var issued = await _db.Select<CouponActivity>()
            .Where(a => merchantId <= 0 || a.MerchantId == merchantId)
            .Where(a => platformId <= 0 || a.PlatformId == platformId)
            .SumAsync(a => a.ClaimQuantity)
            .ConfigureAwait(false);

        // 领取按「拿到券」的时刻算
        var received = await _db.Select<UserCoupon>()
            .Where(a => a.ReceiveAt >= from && a.ReceiveAt < to)
            .CountAsync(ct)
            .ConfigureAwait(false);

        // 核销按「真正用掉」的时刻算。两个时间基准各管各的，
        // 混用会让「月初领、月底用」的券凭空消失。
        var consumed = await _db.Select<UserCoupon>()
            .Where(a => a.ConsumeAt != null && a.ConsumeAt >= from && a.ConsumeAt < to)
            .CountAsync(ct)
            .ConfigureAwait(false);

        var discount = await _db.Select<UserCoupon>()
            .Where(a => a.ConsumeAt != null && a.ConsumeAt >= from && a.ConsumeAt < to)
            .SumAsync(a => a.DiscountAmount)
            .ConfigureAwait(false);

        // 显式转成 decimal 再除：long / long 在 C# 里是**整数除法**，
        // 17 / 40 会变成 0，核销率永远是 0 或 1。
        var rate = received > 0
            ? Math.Round((decimal)consumed / received, 4, MidpointRounding.AwayFromZero)
            : 0m;

        return new CouponReportAggregate(
            decimal.ToInt64(issued),
            received,
            consumed,
            rate,
            Math.Round(discount, 2, MidpointRounding.AwayFromZero));
    }

    /// <inheritdoc />
    public async Task<(List<CouponTemplate> Items, long Total)> PageTemplatesAsync(
        int page, int pageSize, string keyword, int couponType, int status, long platformId,
        CancellationToken ct = default)
    {
        var select = _db.Select<CouponTemplate>()
            .Where(a => couponType <= 0 || a.CouponType == couponType)
            .Where(a => status <= 0 || a.Status == status)
            .Where(a => platformId <= 0 || a.PlatformId == platformId);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            select = select.Where(a => a.TemplateName.Contains(kw));
        }

        var total = await select.CountAsync(ct).ConfigureAwait(false);

        // 排序：显式排序值在前，再按 Id 兜底。
        // 少了那个 Id：两个模板 SortOrder 相同时顺序由数据库决定，翻页会重复或漏行。
        var items = await select
            .OrderBy(a => a.SortOrder)
            .OrderByDescending(a => a.Id)
            .Page(page, pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, total);
    }

    /// <inheritdoc />
    public Task<int> UpdateTemplateAsync(CouponTemplate template, CancellationToken ct = default)
        // 显式列出要更新的列：IssuedQuantity / PlatformId / MerchantId 都不该由后台表单改。
        // 用 SetDtoIgnore 的话，多写一个属性到命令上就会被顺带写进去——
        // 而 IssuedQuantity 一旦能被改，报表的「已发放」就成了可以手工编造的数字。
        => _db.Update<CouponTemplate>()
            .Where(a => a.Id == template.Id)
            .Set(a => new CouponTemplate
            {
                TemplateName = template.TemplateName,
                CouponType = template.CouponType,
                ThresholdAmount = template.ThresholdAmount,
                DiscountAmount = template.DiscountAmount,
                DiscountRate = template.DiscountRate,
                GiftTemplateId = template.GiftTemplateId,
                ValidDays = template.ValidDays,
                TotalQuantity = template.TotalQuantity,
                PerUserLimit = template.PerUserLimit,
                PerOrderLimit = template.PerOrderLimit,
                SortOrder = template.SortOrder,
                Status = template.Status,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public Task<int> DeleteTemplateAsync(long templateId, CancellationToken ct = default)
        => _db.Update<CouponTemplate>()
            .Where(a => a.Id == templateId)
            .Set(a => new CouponTemplate { IsDeleted = true, DeletedAt = DateTime.UtcNow })
            .ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public async Task<(List<CouponActivity> Items, long Total)> PageActivitiesAsync(
        int page, int pageSize, string keyword, int status, long platformId,
        CancellationToken ct = default)
    {
        var select = _db.Select<CouponActivity>()
            .Where(a => status <= 0 || a.Status == status)
            .Where(a => platformId <= 0 || a.PlatformId == platformId);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            select = select.Where(a => a.ActivityName.Contains(kw));
        }

        var total = await select.CountAsync(ct).ConfigureAwait(false);
        var items = await select
            .OrderBy(a => a.SortOrder)
            .OrderByDescending(a => a.Id)
            .Page(page, pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, total);
    }

    /// <inheritdoc />
    public Task<int> UpdateActivityAsync(CouponActivity activity, CancellationToken ct = default)
        => _db.Update<CouponActivity>()
            .Where(a => a.Id == activity.Id)
            .Set(a => new CouponActivity
            {
                ActivityName = activity.ActivityName,
                TemplateId = activity.TemplateId,
                ClaimStartTime = activity.ClaimStartTime,
                ClaimEndTime = activity.ClaimEndTime,
                ClaimQuantity = activity.ClaimQuantity,
                PerUserLimit = activity.PerUserLimit,
                TargetType = activity.TargetType,
                Targets = activity.Targets,
                SortOrder = activity.SortOrder,
                Status = activity.Status,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public async Task<(List<UserCoupon> Items, long Total)> PageUserCouponsAsync(
        int page, int pageSize, int status, long templateId, string orderNo, string keyword,
        CancellationToken ct = default)
    {
        var select = _db.Select<UserCoupon>()
            .Where(a => status <= 0 || a.Status == status)
            .Where(a => templateId <= 0 || a.TemplateId == templateId);

        if (!string.IsNullOrWhiteSpace(orderNo))
        {
            var no = orderNo.Trim();
            select = select.Where(a => a.OrderNo == no);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            select = select.Where(a => a.CouponCode.Contains(kw));
        }

        var total = await select.CountAsync(ct).ConfigureAwait(false);

        // 领取时间倒序：核销记录页是「最近发生了什么」，不是「按模板分组」。
        var items = await select
            .OrderByDescending(a => a.ReceiveAt)
            .OrderByDescending(a => a.Id)
            .Page(page, pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<List<CouponTemplate>> ListTemplatesByIdsAsync(
        IReadOnlyCollection<long> templateIds, CancellationToken ct = default)
        => await _db.Select<CouponTemplate>()
            .Where(a => templateIds.Contains(a.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
}
