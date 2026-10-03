using StackExchange.Redis;

namespace Collaboration.Domain.Infrastructure;

/// <summary>
/// 雪花 workerId 的**租约**持有者：一个进程占住一个槽位，并定期续租。
/// </summary>
/// <remarks>
/// <para><b>为什么不能用 INCR 自增</b>（这是本项目踩过的坑）：</para>
/// <para>
/// 朴素做法是 <c>INCR snowflake:worker:UserService</c>，返回 1 就是 workerId 1。
/// 它在多实例下确实能保证不重号，但<b>单调递增且不回绕</b>：开发机一天重启服务十几次，
/// 跑满 64 次之后这个 key 就永远大于 63，服务<b>从此再也起不来</b>，
/// 而且唯一的「修复」是手工去 Redis 删 key。生产环境滚动发布几十次之后也是同样的结局。
/// </para>
/// <para>
/// 租约模型把「分配」换成「<b>抢占空闲槽位</b>」：槽位 0~63 各对应一个带 TTL 的 key，
/// <c>SET NX</c> 抢到谁就是谁的，抢不到就往后找。进程正常退出或异常消失后，
/// key 到期自动删除，槽位被后来者复用。于是：
/// <list type="bullet">
/// <item>同时存活的实例一定拿到不同槽位（NX 保证），不会撞号；</item>
/// <item>重启多少次都能起来（槽位会释放），没有「用尽」这个状态。</item>
/// </list>
/// </para>
/// <para>
/// 槽位复用是否安全：雪花 Id 里含毫秒时间戳，A 进程退出到 B 进程复用同一槽位之间至少隔了几秒，
/// 两者生成 Id 时的时间戳区间不重叠，Id 不会重复。这是租约方案成立的前提。
/// </para>
/// <para>
/// 续租用后台 <see cref="Timer"/> 而不是让业务代码顺手续期：续租失败只说明网络抖动，
/// 真正致命的是租约到期被别人抢走，所以续租间隔远小于 TTL，容忍连续几次失败。
/// </para>
/// </remarks>
public sealed class WorkerIdLease : IDisposable
{
    /// <summary>租约时长。取 90 秒：进程消失后最多 90 秒槽位释放，对重启体验无感。</summary>
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(90);

    /// <summary>续租间隔。约 TTL 的 1/3，允许连续两次续租失败仍不丢槽位。</summary>
    private static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(30);

    private static Timer? _renewTimer;
    private static readonly SemaphoreSlim RenewGate = new(1, 1);

    private readonly IDatabase _redis;
    private readonly string _key;
    private readonly string _token;
    private bool _disposed;

    private WorkerIdLease(IDatabase redis, string key, string token)
    {
        _redis = redis;
        _key = key;
        _token = token;
    }

    /// <summary>占住的 workerId。</summary>
    public ushort WorkerId { get; private init; }

    /// <summary>租约对应的 Redis key，便于排障时直接查。</summary>
    public string Key => _key;

    /// <summary>
    /// 抢占一个空闲槽位并启动续租。
    /// </summary>
    /// <param name="redis">Redis 数据库句柄。</param>
    /// <param name="keyPrefix">key 前缀，最终 key 为 {前缀}:{appName}:{槽位}。</param>
    /// <param name="appName">服务名。</param>
    /// <param name="upperBound">槽位总数（不含上界），即 workerId 可用范围。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>持有租约的对象。</returns>
    /// <exception cref="InvalidOperationException">所有槽位都被未过期的租约占满。</exception>
    public static async Task<WorkerIdLease> AcquireAsync(
        IDatabase redis,
        string keyPrefix,
        string appName,
        ushort upperBound,
        CancellationToken ct = default)
    {
        if (upperBound == 0)
        {
            throw new InvalidOperationException("雪花 workerId 上限不能为 0。");
        }

        // 令牌带上进程号：只看值认不出「同一个进程续租」和「另一个进程抢同一个槽位」，
        // 续租时据此判断槽位是否已被抢走。
        var token = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        var occupied = new List<string>(upperBound);

        for (ushort slot = 0; slot < upperBound; slot++)
        {
            ct.ThrowIfCancellationRequested();
            var key = $"{keyPrefix}:{appName}:{slot}";

            if (await redis.StringSetAsync(key, token, LeaseTtl, When.NotExists).ConfigureAwait(false))
            {
                var lease = new WorkerIdLease(redis, key, token) { WorkerId = slot };
                lease.StartRenewal();
                return lease;
            }

            occupied.Add($"  {slot} 号槽位已被占用");
        }

        throw new InvalidOperationException(
            $"雪花 workerId 槽位已用满：{appName} 需要 1 个，上限 {upperBound}。" +
            $"说明有 {upperBound} 个实例同时在跑或进程异常退出后租约尚未过期。" +
            $"占用情况：{Environment.NewLine}{string.Join(Environment.NewLine, occupied)}");
    }

    /// <summary>释放槽位并停掉续租。进程正常退出时调用，让槽位立刻可被复用。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRenewal();

        // 只删自己那个令牌：万一租约早过期被别人抢了，这里绝不能把别人的槽位删掉
        var script = @"
            if redis.call('get', KEYS[1]) == ARGV[1] then
                return redis.call('del', KEYS[1])
            end
            return 0";
        try
        {
            _redis.ScriptEvaluate(script, [_key], [_token]);
        }
        catch (Exception)
        {
            // 释放失败不影响正确性：key 到期后槽位一样会被回收。
            // 这里刻意吞掉异常——Dispose 里抛异常会让进程退出路径反而失败。
        }
    }

    /// <summary>启动后台续租定时器。</summary>
    private void StartRenewal()
    {
        lock (RenewGate)
        {
            if (_renewTimer is null)
            {
                _renewTimer = new Timer(_ => RenewAll(), null, RenewInterval, RenewInterval);
            }
        }
    }

    /// <summary>停掉续租定时器。定时器已停止时不用等它，避免进程退出被拖住。</summary>
    private void StopRenewal()
    {
        lock (RenewGate)
        {
            _renewTimer?.Dispose();
            _renewTimer = null;
        }
    }

    /// <summary>
    /// 续租：只有 key 上的令牌仍是自己的才延长 TTL。
    /// </summary>
    /// <remarks>
    /// 令牌比对是必须的：TTL 过期后槽位可能已被别的进程抢走，
    /// 此时无脑 <c>EXPIRE</c> 会把别人的租约续长，让真正的主人迟迟拿不到槽位。
    /// </remarks>
    private void RenewAll()
    {
        try
        {
            var script = @"
                if redis.call('get', KEYS[1]) == ARGV[1] then
                    return redis.call('expire', KEYS[1], ARGV[2])
                end
                return -1";
            _redis.ScriptEvaluate(script, [_key], [_token, (int)LeaseTtl.TotalSeconds]);
        }
        catch (Exception)
        {
            // 续租失败（多为网络抖动）不立即放弃：TTL 90 秒、下次 30 秒后再续，
            // 连续两三次失败都还在容忍范围内。
        }
    }
}
