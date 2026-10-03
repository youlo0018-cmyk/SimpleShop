using Microsoft.Extensions.Logging;
using OrderService.Domain.Ports;
using StackExchange.Redis;

namespace OrderService.Infrastructure.Locking;

/// <summary>基于 Redis 的客户级下单锁（<c>lock:order:create:{customerId}</c>）。</summary>
/// <remarks>
/// <para>加锁用 <c>SET key value NX EX ttl</c>（StackExchange.Redis 的 <c>LockTakeAsync</c> 就是它），
/// 这是**单条命令的原子操作**。用 <c>StringGetAsync</c> 判断再 <c>StringSetAsync</c> 写入是错的：
/// 两个请求会同时读到「不存在」，然后都写成功，两个都以为自己持锁。</para>
///
/// <para>释放用 <c>LockReleaseAsync</c>，它内部是 Lua 比对 value 再删。
/// 不用 <c>KeyDeleteAsync</c> 的原因：锁有 TTL，A 超时后锁自动过期、B 拿到锁，
/// 这时 A 醒来执行 DEL 会把 B 的锁删掉，B 于是与后来的 C 同时持锁——
/// 并发保护在这一刻静默失效，而且不报任何错。</para>
///
/// <para>value 用 <c>Guid</c>：同一个进程内连续两次下单会拿到不同的 value，
/// 万一释放逻辑被写错（比如误用 KeyDelete），至少不会被自己上一次的残留 value 骗过。</para>
/// </remarks>
public sealed class RedisOrderCreateLock : IOrderCreateLock
{
    private const string KeyPrefix = "lock:order:create:";

    private readonly IDatabase _redis;
    private readonly ILogger<RedisOrderCreateLock> _logger;

    /// <summary>构造锁。</summary>
    /// <param name="redis">Redis 数据库句柄。</param>
    /// <param name="logger">日志器。</param>
    public RedisOrderCreateLock(IDatabase redis, ILogger<RedisOrderCreateLock> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IOrderCreateLockHandle?> TryAcquireAsync(
        long customerId, TimeSpan waitFor, TimeSpan ttl, CancellationToken ct = default)
    {
        var key = KeyPrefix + customerId;
        var value = Guid.NewGuid().ToString("N");

        var deadline = DateTime.UtcNow + waitFor;
        var delay = 40;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // LockTakeAsync 内部即 SET NX EX：拿到锁时返回 true。
            if (await _redis.LockTakeAsync(key, value, ttl).ConfigureAwait(false))
            {
                _logger.LogDebug("已取得下单锁 {Key}，持有上限 {Ttl}", key, ttl);
                return new RedisOrderCreateLockHandle(_redis, key, value, _logger);
            }

            if (DateTime.UtcNow >= deadline)
            {
                _logger.LogWarning("等待 {Wait} 后仍未取得下单锁 {Key}", waitFor, key);
                return null;
            }

            // 轮询间隔做轻微递增：并发抢锁时错开重试，减少无效往返。
            await Task.Delay(delay, ct).ConfigureAwait(false);
            if (delay < 200) delay += 20;
        }
    }

    /// <summary>已持有的锁句柄。</summary>
    private sealed class RedisOrderCreateLockHandle : IOrderCreateLockHandle
    {
        private readonly IDatabase _redis;
        private readonly RedisKey _key;
        private readonly RedisValue _value;
        private readonly ILogger _logger;
        private int _released;

        /// <summary>构造句柄。</summary>
        /// <param name="redis">Redis 数据库句柄。</param>
        /// <param name="key">锁键。</param>
        /// <param name="value">本次加锁的唯一值。</param>
        /// <param name="logger">日志器。</param>
        public RedisOrderCreateLockHandle(IDatabase redis, string key, string value, ILogger logger)
        {
            _redis = redis;
            _key = key;
            _value = value;
            _logger = logger;
        }

        /// <summary>释放锁。重复调用只会生效一次。</summary>
        /// <returns>异步任务。</returns>
        public async ValueTask DisposeAsync()
        {
            // Interlocked 保证幂等释放：DisposeAsync 可能被显式调用又被 await using 调用一次。
            if (Interlocked.Exchange(ref _released, 1) != 0) return;

            try
            {
                await _redis.LockReleaseAsync(_key, _value).ConfigureAwait(false);
                _logger.LogDebug("已释放下单锁 {Key}", _key.ToString());
            }
            catch (Exception ex)
            {
                // 释放失败不抛：锁有 TTL，最多卡住后续下单 30 秒，
                // 抛出去会把「下单成功了」变成「接口报错」，客户端重试又变成重复单，得不偿失。
                _logger.LogError(ex, "释放下单锁 {Key} 失败，将等待 TTL 自然过期", _key.ToString());
            }
        }
    }
}