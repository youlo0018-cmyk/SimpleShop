using Collaboration.Domain.Infrastructure;
using Collaboration.Domain.Repository;
using FreeSql;
using Npgsql;
using PointService.Domain.Entities;
using PointService.Domain.IRepository;
using PointService.Domain.Services;

namespace PointService.Infrastructure.Repository;

/// <summary>积分仓储实现。</summary>
/// <remarks>
/// <para><b>幂等</b>一律靠数据库唯一索引（point_record 的 customer_id + biz_no + action），
/// 而不是「先查再插」：并发下两个请求可能都查不到然后都插，
/// 重复发放积分比少发严重得多（用户会真的拿到双倍）。
/// 撞唯一键时识别为「已经处理过」，返回首次结果且不报错——
/// 重复请求是正常业务（订单重试、消息重投），让上游以为失败只会引发更多重试。</para>
///
/// <para><b>并发</b>用条件更新：账户的 available / frozen 必须仍等于事务里读到的值才更新。
/// 不加这个条件，两个并发请求各自算新值、后写的覆盖先写的。</para>
///
/// <para>流水里的「变动前 / 变动后」取自同一份读到的原值与算出的新值，
/// 不从更新后的对象反推——反推会把「变动前」写成「变动后」。</para>
/// </remarks>
public sealed class PointRepository : CrudRepository<PointAccount>, IPointRepository
{
    private readonly IFreeSql _db;
    private readonly IPointRuleProvider _rules;

    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册全局过滤的 FreeSql 单例。</param>
    /// <param name="rules">积分规则提供器。后台可改，所以不能直接读常量了。</param>
    public PointRepository(IFreeSql freeSql, IPointRuleProvider rules) : base(freeSql)
    {
        _db = freeSql;
        _rules = rules;
    }

    /// <inheritdoc />
    public async Task<PointOutcome> EarnAsync(
        long customerId, string source, long quantity, string bizNo, string action = PointActions.Earn,
        string remark = "", int validDays = PointRules.ValidDays, CancellationToken ct = default)
    {
        if (quantity <= 0)
        {
            return new PointOutcome(false, false, 0, 0, Error: "发放数量必须为正数");
        }

        // 规则在**进事务之前**取好：事务体是同步委托，里面不能 await，
        // 而在事务里查配置表等于把配置读取的失败概率算进积分发放的成功率。
        var rules = await _rules.GetAsync(ct).ConfigureAwait(false);

        return await RunIdempotentAsync(customerId, bizNo, action, ct, () =>
        {
            var account = GetOrCreateAccount(customerId, out var created);
            var before = Snapshot(account);

            // 余额上限：超出部分截断不入账（BUSINESS.md 13.7）
            var room = Math.Max(0, rules.BalanceCap - before.Available);
            var actual = Math.Min(quantity, room);
            var capped = actual < quantity;

            var after = before with
            {
                Available = before.Available + actual,
                TotalEarned = before.TotalEarned + actual
            };

            // actual 为 0 时不碰账户（已经是上限），但仍写流水，便于运营知道这笔被截断了
            if (actual > 0)
            {
                Commit(account, before, after, now: DateTime.UtcNow);
            }

            var expireAt = DateTime.UtcNow.AddDays(validDays);
            if (actual > 0)
            {
                _db.Insert(new PointLot
                {
                    Id = SnowflakeId.NewId(),
                    CreatedAt = DateTime.UtcNow,
                    CustomerId = customerId,
                    Source = source,
                    BizNo = bizNo,
                    Total = actual,
                    Remaining = actual,
                    ExpireAt = expireAt
                }).ExecuteAffrows();
            }

            WriteRecord(customerId, bizNo, action, actual, before, after, actual > 0 ? expireAt : null, remark);

            return new PointOutcome(true, false, after.Available, after.Frozen, after.TotalEarned, after.TotalUsed, capped);
        });
    }

    /// <inheritdoc />
    public async Task<PointOutcome> LockAsync(
        long customerId, string bizNo, long quantity, string remark = "", CancellationToken ct = default)
    {
        if (quantity <= 0)
        {
            return new PointOutcome(false, false, 0, 0, Error: "冻结数量必须为正数");
        }

        return await RunIdempotentAsync(customerId, bizNo, PointActions.Lock, ct, () =>
        {
            var account = GetOrCreateAccount(customerId, out _);
            var before = Snapshot(account);

            if (before.Available < quantity)
            {
                return new PointOutcome(false, false, before.Available, before.Frozen, before.TotalEarned, before.TotalUsed,
                    Error: $"可用积分不足（现有 {before.Available}，需要 {quantity}）");
            }

            // FIFO：先到期先用。取消 / 超时退回时按这个明细原路还回去
            var lots = _db.Select<PointLot>()
                .Where(a => a.CustomerId == customerId && a.Remaining > 0 && a.ExpireAt > DateTime.UtcNow)
                .OrderBy(a => a.ExpireAt).OrderBy(a => a.Id)
                .ToList();

            var lockRow = new PointLock
            {
                Id = SnowflakeId.NewId(),
                CreatedAt = DateTime.UtcNow,
                CustomerId = customerId,
                BizNo = bizNo,
                Quantity = quantity,
                Status = PointLockStatus.Frozen
            };
            _db.Insert(lockRow).ExecuteAffrows();

            var need = quantity;
            foreach (var lot in lots)
            {
                if (need <= 0) break;

                var take = (int)Math.Min(lot.Remaining, need);
                _db.Update<PointLot>().Where(a => a.Id == lot.Id)
                    .Set(a => new PointLot { Remaining = a.Remaining - take })
                    .ExecuteAffrows();

                _db.Insert(new PointLockLot
                {
                    Id = SnowflakeId.NewId(),
                    LockId = lockRow.Id,
                    LotId = lot.Id,
                    Quantity = take,
                    CreatedAt = DateTime.UtcNow
                }).ExecuteAffrows();

                need -= take;
            }

            if (need > 0)
            {
                return new PointOutcome(false, false, before.Available, before.Frozen, before.TotalEarned, before.TotalUsed,
                    Error: $"可用积分批次不足，缺少 {need} 分（可能有过期积分尚未被任务清理）");
            }

            var after = before with
            {
                Available = before.Available - quantity,
                Frozen = before.Frozen + quantity
            };
            Commit(account, before, after);
            WriteRecord(customerId, bizNo, PointActions.Lock, quantity, before, after, null, remark);

            return new PointOutcome(true, false, after.Available, after.Frozen, after.TotalEarned, after.TotalUsed);
        });
    }

    /// <inheritdoc />
    public async Task<PointOutcome> UnfreezeAsync(
        long customerId, string bizNo, string remark = "", CancellationToken ct = default)
    {
        return await RunIdempotentAsync(customerId, bizNo, PointActions.Unfreeze, ct, () =>
        {
            var account = RequireAccount(customerId);
            if (account is null) return Fail(customerId, "积分账户不存在");
            var before = Snapshot(account);

            var lockRow = _db.Select<PointLock>()
                .Where(a => a.CustomerId == customerId && a.BizNo == bizNo).First();

            if (lockRow is null)
            {
                return new PointOutcome(false, false, before.Available, before.Frozen, before.TotalEarned, before.TotalUsed, Error: "没有找到对应的冻结记录");
            }

            // 已经实扣 / 已解冻过，再来一次就是重复操作
            if (lockRow.Status != PointLockStatus.Frozen)
            {
                return new PointOutcome(true, true, before.Available, before.Frozen, before.TotalEarned, before.TotalUsed);
            }

            if (before.Frozen < lockRow.Quantity)
            {
                return new PointOutcome(false, false, before.Available, before.Frozen, before.TotalEarned, before.TotalUsed, Error: "冻结积分不足，无法解冻");
            }

            // 退回原发放批次，**不重新计算有效期**（BUSINESS.md 13.4）
            var links = _db.Select<PointLockLot>().Where(a => a.LockId == lockRow.Id).ToList();
            foreach (var link in links)
            {
                _db.Update<PointLot>().Where(a => a.Id == link.LotId)
                    .Set(a => new PointLot { Remaining = a.Remaining + link.Quantity })
                    .ExecuteAffrows();
            }

            var after = before with
            {
                Available = before.Available + lockRow.Quantity,
                Frozen = before.Frozen - lockRow.Quantity
            };
            Commit(account, before, after);

            _db.Update<PointLock>().Where(a => a.Id == lockRow.Id)
                .Set(a => new PointLock { Status = PointLockStatus.Unfrozen }).ExecuteAffrows();

            WriteRecord(customerId, bizNo, PointActions.Unfreeze, lockRow.Quantity, before, after, null, remark);
            return new PointOutcome(true, false, after.Available, after.Frozen, after.TotalEarned, after.TotalUsed);
        });
    }

    /// <inheritdoc />
    public async Task<PointOutcome> ConsumeAsync(
        long customerId, string bizNo, string remark = "", CancellationToken ct = default)
    {
        return await RunIdempotentAsync(customerId, bizNo, PointActions.Consume, ct, () =>
        {
            var account = RequireAccount(customerId);
            if (account is null) return Fail(customerId, "积分账户不存在");
            var before = Snapshot(account);

            var lockRow = _db.Select<PointLock>()
                .Where(a => a.CustomerId == customerId && a.BizNo == bizNo).First();

            if (lockRow is null)
            {
                return new PointOutcome(false, false, before.Available, before.Frozen, before.TotalEarned, before.TotalUsed, Error: "没有找到对应的冻结记录");
            }

            if (lockRow.Status == PointLockStatus.Consumed)
            {
                return new PointOutcome(true, true, before.Available, before.Frozen, before.TotalEarned, before.TotalUsed);
            }

            if (lockRow.Status != PointLockStatus.Frozen)
            {
                return new PointOutcome(false, false, before.Available, before.Frozen, before.TotalEarned, before.TotalUsed,
                    Error: "该冻结批次已解冻或已回收，不能实扣");
            }

            if (before.Frozen < lockRow.Quantity)
            {
                return new PointOutcome(false, false, before.Available, before.Frozen, before.TotalEarned, before.TotalUsed, Error: "冻结积分不足");
            }

            // 实扣：钱已付出，这笔积分就此消失，发放批次不退还
            var after = before with
            {
                Frozen = before.Frozen - lockRow.Quantity,
                TotalUsed = before.TotalUsed + lockRow.Quantity
            };
            Commit(account, before, after);

            _db.Update<PointLock>().Where(a => a.Id == lockRow.Id)
                .Set(a => new PointLock { Status = PointLockStatus.Consumed }).ExecuteAffrows();

            WriteRecord(customerId, bizNo, PointActions.Consume, -lockRow.Quantity, before, after, null, remark);
            return new PointOutcome(true, false, after.Available, after.Frozen, after.TotalEarned, after.TotalUsed);
        });
    }

    /// <inheritdoc />
    public async Task<PointOutcome> RefundAsync(
        long customerId, string bizNo, decimal refundRatio, string remark = "", CancellationToken ct = default)
    {
        if (refundRatio <= 0m || refundRatio > 1m)
        {
            return new PointOutcome(false, false, 0, 0, Error: "退款比例必须在 0 ~ 1 之间");
        }

        return await RunIdempotentAsync(customerId, bizNo, PointActions.Refund, ct, () =>
        {
            var account = RequireAccount(customerId);
            if (account is null) return Fail(customerId, "积分账户不存在");
            var before = Snapshot(account);

            var lockRow = _db.Select<PointLock>()
                .Where(a => a.CustomerId == customerId && a.BizNo == bizNo).First();

            if (lockRow is null)
            {
                return new PointOutcome(false, false, before.Available, before.Frozen, before.TotalEarned, before.TotalUsed, Error: "没有找到对应的冻结记录");
            }

            // 部分退款按比例回收，**向上取整**（BUSINESS.md 10.3）。
            // 向上取整对用户不利、对平台有利：退钱少退一点时积分也多扣一点。
            // 同一笔订单多次退款要累计，所以先扣掉已回收的部分。
            var want = (long)Math.Ceiling(lockRow.Quantity * (double)refundRatio);
            if (want > lockRow.Quantity) want = lockRow.Quantity;

            var refunded = _db.Select<PointRecord>()
                .Where(a => a.CustomerId == customerId && a.BizNo == bizNo && a.Action == PointActions.Refund)
                .ToList()
                .Sum(a => a.Quantity);

            var actual = want - refunded;
            if (actual <= 0)
            {
                return new PointOutcome(true, true, before.Available, before.Frozen, before.TotalEarned, before.TotalUsed);
            }

            // 退回原发放批次，不重新计算有效期
            var links = _db.Select<PointLockLot>().Where(a => a.LockId == lockRow.Id).ToList();
            var remaining = actual;
            foreach (var link in links)
            {
                if (remaining <= 0) break;

                var back = (int)Math.Min(link.Quantity, remaining);
                remaining -= back;
                _db.Update<PointLot>().Where(a => a.Id == link.LotId)
                    .Set(a => new PointLot { Remaining = a.Remaining + back })
                    .ExecuteAffrows();
            }

            var after = before with
            {
                Available = before.Available + actual,
                TotalUsed = Math.Max(0, before.TotalUsed - actual)
            };
            Commit(account, before, after);

            _db.Update<PointLock>().Where(a => a.Id == lockRow.Id)
                .Set(a => new PointLock { Status = PointLockStatus.Refunded }).ExecuteAffrows();

            WriteRecord(customerId, bizNo, PointActions.Refund, actual, before, after, null, remark);
            return new PointOutcome(true, false, after.Available, after.Frozen, after.TotalEarned, after.TotalUsed);
        });
    }

    /// <inheritdoc />
    public async Task<PointOutcome> ExpireLotAsync(long lotId, CancellationToken ct = default)
    {
        var lot = await _db.Select<PointLot>().Where(a => a.Id == lotId).FirstAsync(ct);
        if (lot is null || lot.Remaining <= 0)
        {
            return new PointOutcome(false, false, 0, 0, Error: "批次不存在或已清空");
        }

        // 过期批次用批次 Id 当单号，天然幂等：重复扫到同一批次不会重复扣
        return await RunIdempotentAsync(lot.CustomerId, $"EXP-{lot.Id}", PointActions.Expire, ct, () =>
        {
            var fresh = _db.Select<PointLot>().Where(a => a.Id == lotId).First();
            if (fresh is null || fresh.Remaining <= 0)
            {
                return new PointOutcome(true, true, 0, 0);
            }

            var account = RequireAccount(fresh.CustomerId);
            if (account is null) return Fail(fresh.CustomerId, "积分账户不存在");
            var before = Snapshot(account);

            _db.Update<PointLot>().Where(a => a.Id == lotId)
                .Set(a => new PointLot { Remaining = 0 }).ExecuteAffrows();

            // 过期只能从 available 扣：已冻结的积分属于在途订单，不能凭空消失
            var deduct = Math.Min(before.Available, fresh.Remaining);
            var after = before with
            {
                Available = before.Available - deduct,
                TotalUsed = before.TotalUsed + deduct
            };
            Commit(account, before, after);

            WriteRecord(fresh.CustomerId, $"EXP-{lotId}", PointActions.Expire, -deduct, before, after,
                fresh.ExpireAt, "积分到期");

            return new PointOutcome(true, false, after.Available, after.Frozen, after.TotalEarned, after.TotalUsed);
        });
    }

    /// <inheritdoc />
    public async Task<(PointOutcome Outcome, int Streak, long Reward, bool AlreadySigned)> SignInAsync(
        long customerId, DateOnly localDate, CancellationToken ct = default)
    {
        // 单号就用日期：同一天重复点击天然撞同一个幂等键
        var bizNo = $"SIGN-{localDate:yyyyMMdd}";
        var streak = 0;
        var reward = 0L;
        var already = false;
        var result = new PointOutcome(false, false, 0, 0, Error: "未执行");

        // 同 EarnAsync：规则必须在进事务前取好（事务体是同步委托，不能 await）。
        var rules = await _rules.GetAsync(ct).ConfigureAwait(false);

        try
        {
            await Task.Run(() => _db.Transaction(() =>
            {
                var account = GetOrCreateAccount(customerId, out _);
                var today = localDate.ToDateTime(TimeOnly.MinValue);

                // 当日幂等：上次签到就是今天 → 直接返回，不重复发
                if (account.SignLastDate.HasValue && account.SignLastDate.Value.Date == today)
                {
                    already = true;
                    streak = account.SignStreak;
                    reward = account.SignStreak >= 1 && account.SignStreak <= rules.SignInRewards.Count
                        ? rules.SignInRewards[account.SignStreak - 1]
                        : 0;
                    result = new PointOutcome(true, true, account.Available, account.Frozen, account.TotalEarned, account.TotalUsed);
                    return;
                }

                // 断签即连续天数清零：昨天没签就从第 1 天重新开始（无保底，BUSINESS.md 13.6）
                var signedYesterday = account.SignLastDate.HasValue
                    && account.SignLastDate.Value.Date == today.AddDays(-1);

                streak = signedYesterday ? account.SignStreak + 1 : 1;

                // 7 天一轮：第 8 天回到第 1 天档位
                var slot = ((streak - 1) % rules.SignInRewards.Count) + 1;
                reward = rules.SignInRewards[slot - 1];

                var before = Snapshot(account);
                var room = Math.Max(0, rules.BalanceCap - before.Available);
                var actual = Math.Min(reward, room);
                var capped = actual < reward;

                var after = before with
                {
                    Available = before.Available + actual,
                    TotalEarned = before.TotalEarned + actual
                };

                account.SignStreak = streak;
                account.SignLastDate = today;
                account.UpdatedAt = DateTime.UtcNow;

                _db.Update<PointAccount>()
                    .Where(a => a.Id == account.Id
                                && a.Available == before.Available
                                && a.Frozen == before.Frozen)
                    .Set(a => new PointAccount
                    {
                        Available = after.Available,
                        Frozen = after.Frozen,
                        TotalEarned = after.TotalEarned,
                        TotalUsed = after.TotalUsed,
                        SignStreak = streak,
                        SignLastDate = today,
                        UpdatedAt = DateTime.UtcNow
                    })
                    .ExecuteAffrows();

                if (actual > 0)
                {
                    // 签到积分单独成一批，有效期与普通积分一致（BUSINESS.md 13.6）。
                    var expireAt = DateTime.UtcNow.AddDays(rules.ValidDays);
                    _db.Insert(new PointLot
                    {
                        Id = SnowflakeId.NewId(),
                        CreatedAt = DateTime.UtcNow,
                        CustomerId = customerId,
                        Source = PointSources.SignIn,
                        BizNo = bizNo,
                        Total = actual,
                        Remaining = actual,
                        ExpireAt = expireAt
                    }).ExecuteAffrows();

                    WriteRecord(customerId, bizNo, PointActions.SignIn, actual, before, after, expireAt, "每日签到");
                }

                result = new PointOutcome(true, false, after.Available, after.Frozen, after.TotalEarned, after.TotalUsed, capped);
            }), ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            already = true;
            var acc = await _db.Select<PointAccount>().Where(a => a.CustomerId == customerId).FirstAsync(ct);
            result = new PointOutcome(true, true, acc?.Available ?? 0, acc?.Frozen ?? 0);
        }

        return (result, streak, reward, already);
    }

    /// <inheritdoc />
    public async Task<PointAccount?> GetAccountAsync(long customerId, CancellationToken ct = default)
    {
        // FreeSql 的 FirstAsync 声明成非空，实际查不到会返回 null，这里如实处理
        var account = await _db.Select<PointAccount>().Where(a => a.CustomerId == customerId).FirstAsync(ct);
        return account;
    }

    /// <inheritdoc />
    public async Task<(List<PointRecord> Items, long Total)> QueryRecordsAsync(
        long customerId, int page, int pageSize, CancellationToken ct = default)
    {
        var select = _db.Select<PointRecord>().Where(a => a.CustomerId == customerId);
        var total = await select.CountAsync(ct);
        var items = await select.OrderByDescending(a => a.Id).Page(page, pageSize).ToListAsync(ct);
        return (items, total);
    }

    /// <inheritdoc />
    public async Task<(List<PointRecord> Items, long Total)> QueryRecordsAdminAsync(
        int page, int pageSize, long customerId, string action, string bizNo,
        CancellationToken ct = default)
    {
        var select = _db.Select<PointRecord>()
            .Where(a => customerId <= 0 || a.CustomerId == customerId);

        if (!string.IsNullOrWhiteSpace(action))
        {
            var act = action.Trim();
            select = select.Where(a => a.Action == act);
        }

        if (!string.IsNullOrWhiteSpace(bizNo))
        {
            var no = bizNo.Trim();
            select = select.Where(a => a.BizNo == no);
        }

        var total = await select.CountAsync(ct).ConfigureAwait(false);

        // 按发生时间倒序，再按 Id 兜底。后台查流水问的是「刚刚发生了什么」，
        // 而同一毫秒可能有多条（签到 + 发放就在同一次请求里），
        // 少了那个 Id 就会出现翻页时同一行出现在两页。
        var items = await select
            .OrderByDescending(a => a.CreatedAt)
            .OrderByDescending(a => a.Id)
            .Page(page, pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<List<PointLockLot>> GetLockLotsAsync(string bizNo, CancellationToken ct = default)
    {
        var lockRow = _db.Select<PointLock>().Where(a => a.BizNo == bizNo).First();
        if (lockRow is null) return new List<PointLockLot>();
        return await _db.Select<PointLockLot>().Where(a => a.LockId == lockRow.Id).ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<List<PointLot>> GetExpiredLotsAsync(DateTime nowUtc, int limit, CancellationToken ct = default)
        => await _db.Select<PointLot>()
            .Where(a => a.ExpireAt <= nowUtc && a.Remaining > 0)
            .OrderBy(a => a.ExpireAt).OrderBy(a => a.Id)
            .Limit(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);

    // ---------------- 内部辅助 ----------------

    /// <summary>账户余额快照。流水里「变动前 / 变动后」都从这里派生，不用更新后的对象反推。</summary>
    private readonly record struct AccountSnapshot(long Available, long Frozen, long TotalEarned, long TotalUsed);

    private static AccountSnapshot Snapshot(PointAccount a)
        => new(a.Available, a.Frozen, a.TotalEarned, a.TotalUsed);

    /// <summary>
    /// 在事务里跑一段业务逻辑，并统一处理「幂等命中」与「唯一键冲突」。
    /// </summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="bizNo">业务单号。</param>
    /// <param name="action">动作。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="core">业务逻辑，返回结果。</param>
    /// <returns>结果。</returns>
    private async Task<PointOutcome> RunIdempotentAsync(
        long customerId, string bizNo, string action, CancellationToken ct, Func<PointOutcome> core)
    {
        // Db.Transaction 的委托返回 void，结果只能在外面接
        var result = new PointOutcome(false, false, 0, 0, Error: "未执行");

        try
        {
            await Task.Run(() => _db.Transaction(() =>
            {
                // 已经处理过就直接返回首次结果，不重复变更
                var existing = _db.Select<PointRecord>()
                    .Where(a => a.CustomerId == customerId && a.BizNo == bizNo && a.Action == action)
                    .First();

                if (existing is not null)
                {
                    var acc = _db.Select<PointAccount>().Where(a => a.CustomerId == customerId).First();
                    result = new PointOutcome(true, true, acc?.Available ?? existing.AfterAvailable,
                        acc?.Frozen ?? existing.AfterFrozen);
                    return;
                }

                result = core();
            }), ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // 并发下另一个请求刚写了同一条流水
            var acc = await _db.Select<PointAccount>().Where(a => a.CustomerId == customerId).FirstAsync(ct);
            return new PointOutcome(true, true, acc?.Available ?? 0, acc?.Frozen ?? 0);
        }

        return result;
    }

    /// <summary>取账户，不存在返回 null（区别于 GetOrCreateAccount）。</summary>
    private PointAccount? RequireAccount(long customerId)
        => _db.Select<PointAccount>().Where(a => a.CustomerId == customerId).First();

    private PointAccount GetOrCreateAccount(long customerId, out bool created)
    {
        var existing = _db.Select<PointAccount>().Where(a => a.CustomerId == customerId).First();
        if (existing is not null)
        {
            created = false;
            return existing;
        }

        var account = new PointAccount
        {
            Id = SnowflakeId.NewId(),
            CreatedAt = DateTime.UtcNow,
            CustomerId = customerId
        };

        try
        {
            _db.Insert(account).ExecuteAffrows();
            created = true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            created = false;
            account = _db.Select<PointAccount>().Where(a => a.CustomerId == customerId).First()
                ?? throw new InvalidOperationException($"客户 {customerId} 的积分账户并发创建后仍查不到。", ex);
        }

        return account;
    }

    /// <summary>条件更新账户：两个计数都还是读到的值才更新，否则视为并发冲突并让事务回滚。</summary>
    /// <param name="account">本次事务里读到的账户实体。</param>
    /// <param name="before">读到的原值。</param>
    /// <param name="after">算出的新值。</param>
    /// <param name="now">更新时间。</param>
    private void Commit(PointAccount account, AccountSnapshot before, AccountSnapshot after, DateTime? now = null)
    {
        var affected = _db.Update<PointAccount>()
            .Where(a => a.Id == account.Id
                        && a.Available == before.Available
                        && a.Frozen == before.Frozen)
            .Set(a => new PointAccount
            {
                Available = after.Available,
                Frozen = after.Frozen,
                TotalEarned = after.TotalEarned,
                TotalUsed = after.TotalUsed,
                UpdatedAt = now ?? DateTime.UtcNow
            })
            .ExecuteAffrows();

        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"客户 {account.CustomerId} 的积分账户在本次事务期间被其它请求改动，条件更新未命中，事务回滚。");
        }
    }

    private void WriteRecord(
        long customerId, string bizNo, string action, long quantity,
        AccountSnapshot before, AccountSnapshot after, DateTime? lotExpireAt, string remark)
    {
        _db.Insert(new PointRecord
        {
            Id = SnowflakeId.NewId(),
            CreatedAt = DateTime.UtcNow,
            CustomerId = customerId,
            BizNo = bizNo,
            Action = action,
            Quantity = quantity,
            BeforeAvailable = before.Available,
            AfterAvailable = after.Available,
            BeforeFrozen = before.Frozen,
            AfterFrozen = after.Frozen,
            LotExpireAt = lotExpireAt,
            Remark = remark ?? string.Empty
        }).ExecuteAffrows();
    }

    private static PointOutcome Fail(long customerId, string error)
        => new(false, false, 0, 0, Error: error);

    /// <inheritdoc />
    /// <remarks>
    /// 流水里 <c>quantity</c> 是**带符号**的：发放 / 冻结为正，实扣 / 过期为负。
    /// 所以正负要分开处理——直接把 quantity 求和会互相抵消，
    /// 「发 100 扣 100」的结果是 0，看不出任何发生额。
    /// </remarks>
    public async Task<PointReportAggregate> AggregateAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        // 发放 = 所有让可用积分增加的动作。refund 也算发放：
        // 钱退回来了，积分确实回到了用户手里。
        string[] earnActions = [PointActions.Earn, PointActions.SignIn, PointActions.Refund];

        var earned = await Db.Select<PointRecord>()
            .Where(a => a.CreatedAt >= from && a.CreatedAt < to)
            .Where(a => earnActions.Contains(a.Action))
            .SumAsync(a => a.Quantity)
            .ConfigureAwait(false);

        // 实扣与过期在流水里是负数，取负号还原成「消耗了多少」的绝对值
        var consumedRaw = await Db.Select<PointRecord>()
            .Where(a => a.CreatedAt >= from && a.CreatedAt < to)
            .Where(a => a.Action == PointActions.Consume)
            .SumAsync(a => a.Quantity)
            .ConfigureAwait(false);

        var expiredRaw = await Db.Select<PointRecord>()
            .Where(a => a.CreatedAt >= from && a.CreatedAt < to)
            .Where(a => a.Action == PointActions.Expire)
            .SumAsync(a => a.Quantity)
            .ConfigureAwait(false);

        // 冻结与余额是**当前快照**，不看区间：
        // 「冻结总额」问的是「现在有多少积分被占着」，不是「这段时间冻结过多少」。
        var currentFrozen = await Db.Select<PointAccount>().SumAsync(a => a.Frozen)
            .ConfigureAwait(false);

        var currentAvailable = await Db.Select<PointAccount>().SumAsync(a => a.Available)
            .ConfigureAwait(false);

        // FreeSql 的 SumAsync 对 bigint 列返回 **decimal**：PostgreSQL 里
        // sum(bigint) 的结果类型是 numeric，不是 bigint。不显式转换的话，
        // 编译不过；而写成 SumAsync<long> 让 FreeSql 自己转又会多一层不确定。
        // 这里集中转换，顺便把「空表时 sum 返回 null」的情况一并归零。
        return new PointReportAggregate(
            ToLong(earned),
            ToLong(-consumedRaw),
            ToLong(-expiredRaw),
            ToLong(currentFrozen),
            ToLong(currentAvailable + currentFrozen));
    }

    /// <summary>把 SUM 的 decimal 结果转成 long，空值按 0。</summary>
    /// <param name="value">SUM 结果。</param>
    /// <returns>整数值。</returns>
    private static long ToLong(decimal value)
        => decimal.ToInt64(decimal.Round(value, MidpointRounding.AwayFromZero));
}
