using Collaboration.Domain.Infrastructure;
using Collaboration.Domain.Repository;
using Collaboration.Domain.Context;
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

            for (var i = 0; i < quantity; i++)
            {
                // 适用范围取自**券活动**（同一张模板可以由不同活动投放到不同商品范围）；
                // 其余字段取自模板快照。两条发券路径（领券 / 满赠）共用这一个构造方法，
                // 各写一份的话，改了一处另一处会静默停留在旧规则上。
                var coupon = NewCouponFromTemplate(
                    template, customerId, activity.Id, activity.TargetType, activity.Targets, nowUtc, i);

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
                    // 幂等命中也要把**逐行分摊**一起回给订单侧：重试的下单如果拿不到分摊，
                    // 就会退化成「按全行比例自己算」，同一张单两次下单得到两套逐行金额。
                    var alreadyCoupon = _db.Select<UserCoupon>().Where(a => a.Id == existingOccupancy.CouponId).First();
                    result = new CouponOutcome(true, true, existingOccupancy.CouponId, existingOccupancy.DiscountAmount)
                    {
                        LineDiscounts = alreadyCoupon is null
                            ? []
                            : CouponCalculator.AllocateToCoveredLines(
                                alreadyCoupon, lines, existingOccupancy.DiscountAmount)
                    };
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

                // 满赠券的折扣额**本来就是 0**：它不是「不可用」，而是「本单送券」。
                // 一律按 discount <= 0 拒掉的话，券类型 4 的券永远占不上，
                // 付完钱自然也发不出它承诺的赠品券——整条满赠券链路等于不存在。
                var isGift = coupon.CouponType == CouponTypes.Gift;
                var usable = quote.ReachedThreshold && (isGift || quote.DiscountAmount > 0m);

                if (!usable)
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

                // 满赠券要送的那张券，此刻就把「送什么、送几张」落成发放承诺。
                // 支付成功时按承诺发券，而不是那时再回头读模板 ——
                // 运营在用户付款期间改了模板，用户拿到的就不该跟着变。
                if (isGift)
                {
                    var source = _db.Select<CouponTemplate>().Where(a => a.Id == coupon.TemplateId).First();
                    if (source is not null && source.GiftTemplateId > 0)
                    {
                        _db.Insert(new GiftGrant
                        {
                            Id = SnowflakeId.NewId(),
                            CreatedAt = nowUtc,
                            OrderNo = orderNo,
                            CustomerId = customerId,
                            SourceType = GiftGrantSources.Coupon,
                            SourceId = coupon.Id,
                            GiftTemplateId = source.GiftTemplateId,
                            Quantity = 1,
                            Status = GiftGrantStatuses.Pending
                        }).ExecuteAffrows();
                    }
                }

                // 逐行分摊在这里算：只有营销侧知道这张券覆盖了哪几行
                // （订单侧只有「总额」，按全行比例分会把优惠摊到作用域外的行上）。
                result = new CouponOutcome(true, false, coupon.Id, quote.DiscountAmount)
                {
                    LineDiscounts = CouponCalculator.AllocateToCoveredLines(coupon, lines, quote.DiscountAmount)
                };
            }), ct);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            // 唯一索引挡住了重复占同一订单
            var existing = await _db.Select<CouponOccupancy>().Where(a => a.OrderNo == orderNo).FirstAsync(ct);
            if (existing is null) throw;

            var existingCoupon = await _db.Select<UserCoupon>()
                .Where(a => a.Id == existing.CouponId).FirstAsync(ct).ConfigureAwait(false);

            return new CouponOutcome(true, true, existing.CouponId, existing.DiscountAmount)
            {
                LineDiscounts = existingCoupon is null
                    ? []
                    : CouponCalculator.AllocateToCoveredLines(existingCoupon, lines, existing.DiscountAmount)
            };
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
    public async Task<int> RecordGiftGrantsAsync(
        string orderNo, long customerId, IReadOnlyList<GiftGrantRequest> grants,
        DateTime nowUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderNo) || grants.Count == 0) return 0;

        var written = 0;

        await Task.Run(() => _db.Transaction(() =>
        {
            written = 0;

            foreach (var grant in grants)
            {
                if (grant.GiftTemplateId <= 0 || grant.Quantity <= 0) continue;

                // 同一单同一来源只承诺一次。试算会被重放（客户端重试、幂等键撞车），
                // 不判存在性的话付完钱会发两份券。
                var exists = _db.Select<GiftGrant>()
                    .Where(a => a.OrderNo == orderNo
                                && a.SourceType == grant.SourceType
                                && a.SourceId == grant.SourceId)
                    .Any();
                if (exists) continue;

                _db.Insert(new GiftGrant
                {
                    Id = SnowflakeId.NewId(),
                    CreatedAt = nowUtc,
                    OrderNo = orderNo,
                    CustomerId = customerId,
                    SourceType = grant.SourceType,
                    SourceId = grant.SourceId,
                    GiftTemplateId = grant.GiftTemplateId,
                    Quantity = grant.Quantity,
                    Status = GiftGrantStatuses.Pending
                }).ExecuteAffrows();

                written++;
            }
        }), ct);

        return written;
    }

    /// <inheritdoc />
    public async Task<GiftIssueOutcome> IssueGiftGrantsAsync(
        string orderNo, DateTime nowUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderNo)) return new GiftIssueOutcome(0, 0, 0, 0);

        var promised = 0;
        var issued = 0;
        var couponCount = 0;
        var failed = 0;

        await Task.Run(() => _db.Transaction(() =>
        {
            promised = 0;
            issued = 0;
            couponCount = 0;
            failed = 0;

            var all = _db.Select<GiftGrant>().Where(a => a.OrderNo == orderNo).ToList();
            promised = all.Count;

            foreach (var grant in all.Where(a => a.Status == GiftGrantStatuses.Pending))
            {
                var template = _db.Select<CouponTemplate>().Where(a => a.Id == grant.GiftTemplateId).First();

                if (template is null)
                {
                    // 模板被删了，券发不出来。**不能因此让支付失败**：钱已经收了，
                    // 把订单卡在待支付只会让用户付了钱看不到订单。
                    // 记录留在「待发放」，作为可对账的异常信号（接口把条数报给调用方记日志）。
                    failed++;
                    continue;
                }

                // 满赠券没有券活动，适用范围按**全场**快照。
                // 模板本身不带适用范围（那是券活动的字段），所以这里没有更精确的来源可抄。
                for (var i = 0; i < grant.Quantity; i++)
                {
                    var coupon = NewCouponFromTemplate(
                        template, grant.CustomerId, activityId: 0,
                        TargetTypes.All, "[]", nowUtc, i);

                    _db.Insert(coupon).ExecuteAffrows();
                    couponCount++;
                }

                // 已发放数只在有池子的模板上累加。0 表示不限量，没有可累加的池子。
                if (template.TotalQuantity > 0)
                {
                    _db.Update<CouponTemplate>()
                        .Where(a => a.Id == template.Id)
                        .Set(a => new CouponTemplate
                        {
                            IssuedQuantity = template.IssuedQuantity + grant.Quantity
                        })
                        .ExecuteAffrows();
                }

                // 条件更新：只有仍是「待发放」才置为已发放。
                // 重复调用（支付回调重投）时这里影响 0 行，也就不会重复发券。
                _db.Update<GiftGrant>()
                    .Where(a => a.Id == grant.Id && a.Status == GiftGrantStatuses.Pending)
                    .Set(a => new GiftGrant
                    {
                        Status = GiftGrantStatuses.Issued,
                        IssuedAt = nowUtc,
                        UpdatedAt = nowUtc
                    })
                    .ExecuteAffrows();

                issued++;
            }
        }), ct);

        return new GiftIssueOutcome(promised, issued, couponCount, failed);
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

    /// <summary>按模板快照造一张用户券。</summary>
    /// <param name="template">券模板，快照的来源。</param>
    /// <param name="customerId">收券的客户 Id。</param>
    /// <param name="activityId">来源券活动 Id，0 表示不是从券活动发的（满赠）。</param>
    /// <param name="targetType">适用范围类型，见 <see cref="TargetTypes"/>。</param>
    /// <param name="targets">适用范围的 JSON 文本。</param>
    /// <param name="nowUtc">当前 UTC 时间，有效期从这一刻起算。</param>
    /// <param name="index">同一批发券里的序号，只用于券码去重。</param>
    /// <returns>待插入的用户券。</returns>
    /// <remarks>
    /// <b>券快照机制（DATA_SPEC 5.12）</b>：类型 / 门槛 / 优惠额 / 折扣率 / 有效天数
    /// 在发放这一刻复制到用户券自己的字段上，之后模板怎么改都不影响它。
    /// 不这么做的话，运营改一次模板价格，全站已发出的券跟着变价——那是资损级事故。
    /// </remarks>
    private static UserCoupon NewCouponFromTemplate(
        CouponTemplate template, long customerId, long activityId,
        int targetType, string targets, DateTime nowUtc, int index)
        => new()
        {
            Id = SnowflakeId.NewId(),
            CreatedAt = nowUtc,
            CustomerId = customerId,
            TemplateId = template.Id,
            ActivityId = activityId,
            CouponCode = NewCouponCode(nowUtc, index),
            // ↓ 快照：模板改多少次都不影响这张券
            CouponType = template.CouponType,
            ThresholdAmount = template.ThresholdAmount,
            DiscountAmount = template.DiscountAmount,
            DiscountRate = template.DiscountRate,
            ValidDays = template.ValidDays,
            // ↑ 快照结束
            TargetType = targetType,
            Targets = targets,
            Status = CouponStatuses.Unused,
            ExpireAt = nowUtc.AddDays(template.ValidDays),
            ReceiveAt = nowUtc,
            OrderNo = string.Empty
        };

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
    public async Task<long> InsertTemplateAsync(CouponTemplate template, CancellationToken ct = default)
    {
        // Id 与创建时间由服务端生成；已发放数强制从 0 起 ——
        // 那是发放流程累加出来的计数，不是可以填的表单字段。
        //
        // 审计字段显式填：FreeSql 3.5 的 Aop.CurdBefore 在本项目调用链上没有触发，
        // 直接 _db.Insert 会让「创建人 / 最后操作人」全留在 0 ——
        // 配置错了之后查不出是谁建的（DATA_SPEC 2.2 要求后台实体带这两组信息）。
        var ctx = TenantContextHolder.Current;
        template.Id = SnowflakeId.NewId();
        template.CreatedAt = DateTime.UtcNow;
        template.IssuedQuantity = 0;
        template.CreatedById = ctx.UserId;
        template.CreatedByName = ctx.UserName;
        template.OperationId = ctx.UserId;
        template.OperationName = ctx.UserName;

        await _db.Insert(template).ExecuteAffrowsAsync(ct).ConfigureAwait(false);
        return template.Id;
    }

    /// <inheritdoc />
    public Task<int> UpdateTemplateAsync(CouponTemplate template, CancellationToken ct = default)
    {
        // 操作人**只在这里**更新，创建人字段永远不进 SET：
        // 「这个模板是谁建的」一旦被编辑动作覆盖，配置错误就再也查不出是谁引进来的。
        var ctx = TenantContextHolder.Current;

        // 显式列出要更新的列：IssuedQuantity / PlatformId / MerchantId 都不该由后台表单改。
        // 用 SetDtoIgnore 的话，多写一个属性到命令上就会被顺带写进去——
        // 而 IssuedQuantity 一旦能被改，报表的「已发放」就成了可以手工编造的数字。
        return _db.Update<CouponTemplate>()
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
                OperationId = ctx.UserId,
                OperationName = ctx.UserName,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> CountActiveActivitiesByTemplateAsync(long templateId, CancellationToken ct = default)
    {
        // CountAsync 返回 long，契约用 int：一个模板被上千个活动引用是不可能的量级，转换安全
        var count = await _db.Select<CouponActivity>()
            .Where(a => a.TemplateId == templateId && a.Status == 1)
            .CountAsync(ct)
            .ConfigureAwait(false);

        return (int)count;
    }

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
    public async Task<long> InsertActivityAsync(CouponActivity activity, CancellationToken ct = default)
    {
        // 同模板：Id / 创建时间由服务端生成，已领取数强制从 0 起。
        var ctx = TenantContextHolder.Current;
        activity.Id = SnowflakeId.NewId();
        activity.CreatedAt = DateTime.UtcNow;
        activity.ClaimedQuantity = 0;
        activity.CreatedById = ctx.UserId;
        activity.CreatedByName = ctx.UserName;
        activity.OperationId = ctx.UserId;
        activity.OperationName = ctx.UserName;

        await _db.Insert(activity).ExecuteAffrowsAsync(ct).ConfigureAwait(false);
        return activity.Id;
    }

    /// <inheritdoc />
    public Task<int> UpdateActivityAsync(CouponActivity activity, CancellationToken ct = default)
    {
        // 同模板：只覆盖「最后操作人」，创建人保持不变。
        var ctx = TenantContextHolder.Current;

        return _db.Update<CouponActivity>()
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
                OperationId = ctx.UserId,
                OperationName = ctx.UserName,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);
    }

    /// <inheritdoc />
    public async Task<(List<UserCoupon> Items, long Total)> PageUserCouponsAsync(
        int page, int pageSize, int status, long templateId, string orderNo, string keyword,
        long customerId = 0,
        CancellationToken ct = default)
    {
        var select = _db.Select<UserCoupon>()
            .Where(a => status <= 0 || a.Status == status)
            .Where(a => templateId <= 0 || a.TemplateId == templateId)
            .Where(a => customerId <= 0 || a.CustomerId == customerId);

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
