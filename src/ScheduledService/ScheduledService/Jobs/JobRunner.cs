using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ScheduledService.Jobs;

/// <summary>定时任务执行器：按间隔循环执行，并用 Redis 互斥锁保证多实例只有一个在跑。</summary>
/// <remarks>
/// <para><b>为什么需要互斥锁</b>：生产环境通常起两个 Scheduled 实例（高可用）。
/// 不加锁的话两个实例会同时扫同一批超时订单：订单服务侧有条件更新兜底
/// （只有一个能把状态从 10 改成 91），所以不会重复关单，但每个实例都会
/// 把整批订单拉一遍、再对每张失败的单做无用功，而且下游要吃双倍请求。</para>
///
/// <para><b>锁的 TTL 必须大于单轮耗时</b>：TTL 到期后锁自动消失，
/// 此时若原实例还在跑就会出现两个实例同时执行。TTL 设成间隔的数倍就是为此。
/// 用 Lua 比对 value 释放，与下单锁同一套道理——不能直接 DEL，
/// 否则超时释放后 A 醒来把 B 的锁删了。</para>
/// </remarks>
public sealed class JobRunner : BackgroundService
{
    private readonly IEnumerable<IJob> _jobs;
    private readonly IDatabase _redis;
    private readonly ILogger<JobRunner> _logger;

    /// <summary>构造执行器。</summary>
    /// <param name="jobs">全部任务。</param>
    /// <param name="redis">Redis 数据库句柄，用于任务互斥锁。</param>
    /// <param name="logger">日志器。</param>
    public JobRunner(IEnumerable<IJob> jobs, IDatabase redis, ILogger<JobRunner> logger)
    {
        _jobs = jobs;
        _redis = redis;
        _logger = logger;
    }

    /// <summary>主循环。</summary>
    /// <param name="stoppingToken">停止令牌。</param>
    /// <returns>异步任务。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var jobs = _jobs.ToArray();

        if (jobs.Length == 0)
        {
            _logger.LogWarning("没有注册任何定时任务，进程空转");
            return;
        }

        _logger.LogInformation(
            "定时任务已启动，共 {Count} 个：{Jobs}",
            jobs.Length, string.Join('、', jobs.Select(a => $"{a.Name}({a.IntervalSeconds}s)")));

        // 每个任务一条独立循环：某个任务的间隔与耗时不影响其它任务，
        // 也不会因为某个任务慢而把所有任务都拖住。
        var loops = jobs.Select(a => LoopAsync(a, stoppingToken)).ToArray();
        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    /// <summary>单个任务的循环。</summary>
    /// <param name="job">任务。</param>
    /// <param name="ct">停止令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task LoopAsync(IJob job, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, job.IntervalSeconds));
        var lockTtl = TimeSpan.FromSeconds(Math.Max(interval.TotalSeconds * 3, job.LockTtlSeconds));

        // 明确的初始延迟（错峰）：两个都会扫全表的重任务如果都等到整点才第一次跑，
        // 会在同一秒压数据库，连接池打满后彼此超时、互相拖慢。
        // 只有设了 InitialDelaySeconds 的任务会被推迟，其余任务行为不变。
        if (job.InitialDelaySeconds > 0)
        {
            _logger.LogInformation(
                "任务 {Job} 首次执行推迟 {Seconds} 秒（错峰）", job.Name, job.InitialDelaySeconds);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(job.InitialDelaySeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        // 错开首次执行：多个任务如果都等到整点才第一次跑，会在同一秒发出所有请求
        await Task.Delay(Random.Shared.Next(200, 1200), ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            await RunOnceAsync(job, lockTtl, ct).ConfigureAwait(false);

            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止，不是异常
                break;
            }
        }

        _logger.LogInformation("任务 {Job} 已停止", job.Name);
    }

    /// <summary>执行一轮。</summary>
    /// <param name="job">任务。</param>
    /// <param name="lockTtl">互斥锁 TTL。</param>
    /// <param name="ct">停止令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task RunOnceAsync(IJob job, TimeSpan lockTtl, CancellationToken ct)
    {
        var key = "lock:job:" + job.Name;
        var token = Guid.NewGuid().ToString("N");

        bool acquired;
        try
        {
            acquired = await _redis.LockTakeAsync(key, token, lockTtl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Redis 挂了就不跑。宁可这一轮不做，也不能在没有互斥的情况下并发跑：
            // 那正是这个锁要防的场景，而后果（重复处理）事后无法撤销。
            _logger.LogError(ex, "任务 {Job} 取互斥锁失败，本轮跳过", job.Name);
            return;
        }

        if (!acquired)
        {
            _logger.LogDebug("任务 {Job} 已被别的实例持有锁，本轮跳过", job.Name);
            return;
        }

        var startedAt = DateTime.UtcNow;

        try
        {
            var result = await job.ExecuteAsync(ct).ConfigureAwait(false);
            var cost = (DateTime.UtcNow - startedAt).TotalMilliseconds;

            if (result.Succeeded)
            {
                _logger.LogInformation("任务 {Job} 完成（{Cost}ms）：{Summary}", job.Name, cost, result.Summary);
            }
            else
            {
                // 失败不抛：抛了会中断整个循环，后面所有任务都永久停摆
                _logger.LogError("任务 {Job} 失败（{Cost}ms）：{Summary} —— {Error}",
                    job.Name, cost, result.Summary, result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "任务 {Job} 执行异常，本轮跳过", job.Name);
        }
        finally
        {
            try
            {
                await _redis.LockReleaseAsync(key, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 释放失败不抛：锁有 TTL，最多卡一个 TTL 的时间
                _logger.LogWarning(ex, "任务 {Job} 释放互斥锁失败，将等待 TTL 过期", job.Name);
            }
        }
    }
}
