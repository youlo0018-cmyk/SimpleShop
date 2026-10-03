namespace OrderService.Domain.Ports;

/// <summary>
/// 客户级下单互斥锁端口。
/// </summary>
/// <remarks>
/// <para><b>为什么仅有「按幂等键查一次」不够</b>：两个并发请求带着同一个幂等键打进来，
/// 查库那一刻都可能还查不到对方刚写的单，于是双双通过幂等检查，
/// 然后各自去锁一遍库存——库存被双倍冻结，而幂等键的唯一索引只能保证<b>一张单</b>落库，
/// 拦不住另一张单执行过的副作用。</para>
///
/// <para>锁把同一客户的下单动作串行化，第二个请求进不来，第一个跑完释放后它拿到锁、
/// 查到已有订单、直接返回首次结果，这才是幂等的完整语义。</para>
///
/// <para>实现必须用 <c>SET key value NX EX ttl</c> 原子加锁，不能用「先 GET 再 SET」；
/// 释放必须用 Lua 比对 value 后再删，不能直接 DEL——否则 A 的锁超时释放后 B 又拿到了锁，
/// A 醒来把 B 的锁删了，两个人同时以为自己持锁。</para>
/// </remarks>
public interface IOrderCreateLock
{
    /// <summary>尝试为某个客户取得下单锁。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="waitFor">最多等待多久，用于让并发的第二个请求等第一个跑完后再命中幂等。</param>
    /// <param name="ttl">锁的持有上限，防止持锁进程崩溃后锁永久不释放。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>取得锁时返回句柄，<b>调用方必须释放</b>；等待超时仍未取得返回 null。</returns>
    Task<IOrderCreateLockHandle?> TryAcquireAsync(
        long customerId, TimeSpan waitFor, TimeSpan ttl, CancellationToken ct = default);
}

/// <summary>已取得的下单锁句柄。</summary>
/// <remarks>实现为 <see cref="IAsyncDisposable"/>：<c>await using</c> 能保证即使中途抛异常也一定释放。</remarks>
public interface IOrderCreateLockHandle : IAsyncDisposable
{
}