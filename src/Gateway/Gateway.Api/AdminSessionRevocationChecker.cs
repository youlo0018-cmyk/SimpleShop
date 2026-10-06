using System.Globalization;
using Collaboration.Domain.Security;
using StackExchange.Redis;

namespace Gateway.Api;

/// <summary>
/// 后台令牌的吊销检查：比对令牌签发时间与账号的吊销时刻（DATA_SPEC 5.20）。
/// </summary>
/// <remarks>
/// <para>后台 access token 是自包含的，验签通过就代表「这确实是我们签发的、还没过期」，
/// 但它无法表达「签发之后账号被重置了密码」。重置密码时 UserService 往 Redis 写一个吊销时刻，
/// 这里读出来比对 <c>iat</c>：早于吊销时刻的令牌一律拒绝。</para>
///
/// <para><b>比较用 &lt;=（同秒也吊销）</b>：JWT 的 <c>iat</c> 只精确到秒，
/// 所以「同一秒内签发的令牌」这个歧义无法消除，只能选一边：
/// <list type="bullet">
/// <item>用 <c>&lt;</c>：重置密码后同一秒内重新登录能立刻拿到可用令牌，
/// 但攻击者若恰好在重置的同一秒登录，拿到的令牌能活到过期 —— 这是安全侧的口子。</item>
/// <item>用 <c>&lt;=</c>：重置密码后同一秒内登录拿到的令牌也会被拒，用户需再登一次（1 秒后即可）。
/// 这是可用性侧的瑕疵，而且会自己恢复。</item>
/// </list>
/// 吊销是安全控制，选了严格的一边。真要根治得让令牌带上比秒更细的签发时刻，
/// 那属于改动签发链路，不在本条范围内。</para>
///
/// <para><b>Redis 不可用时拒绝（fail-closed）</b>：读不到吊销状态就没法证明令牌是干净的，
/// 此时放行等于「Redis 一挂，所有被吊销的令牌全部复活」。与网关 RBAC 的处理一致，
/// 这里返回 503 让调用方知道是依赖故障，而不是伪装成令牌无效的 401。</para>
/// </remarks>
public sealed class AdminSessionRevocationChecker
{
    private readonly IDatabase _redis;
    private readonly ILogger<AdminSessionRevocationChecker> _logger;

    /// <summary>构造检查器。</summary>
    /// <param name="redis">Redis 连接。</param>
    /// <param name="sharedDatabase">共享逻辑库索引，必须与 UserService 写入时一致。</param>
    /// <param name="logger">日志器。</param>
    public AdminSessionRevocationChecker(
        IConnectionMultiplexer redis, int sharedDatabase, ILogger<AdminSessionRevocationChecker> logger)
    {
        _redis = redis.GetDatabase(sharedDatabase);
        _logger = logger;
    }

    /// <summary>检查结果。</summary>
    /// <param name="Revoked">令牌是否已被吊销。</param>
    /// <param name="StoreUnavailable">吊销存储是否不可用。为 true 时调用方应拒绝而不是放行。</param>
    public readonly record struct RevocationResult(bool Revoked, bool StoreUnavailable);

    /// <summary>检查某账号的令牌是否已被吊销。</summary>
    /// <param name="userId">账号 Id（令牌 sub 声明）。</param>
    /// <param name="issuedAtUnixSeconds">令牌签发时间（iat 声明），取不到时传 null。</param>
    /// <returns>检查结果。</returns>
    public async Task<RevocationResult> CheckAsync(long userId, long? issuedAtUnixSeconds)
    {
        // 拿不到 iat 就没法判断新旧。放行会漏掉被吊销的令牌，拒绝会误伤所有令牌，
        // 所以按「存储不可用」处理，让调用方回 503 —— 这是配置/签发链路的 bug，要暴露出来。
        if (issuedAtUnixSeconds is null)
        {
            _logger.LogError("后台令牌缺少 iat 声明，账号 {UserId} 的吊销状态无法判定", userId);
            return new RevocationResult(false, true);
        }

        RedisValue stored;
        try
        {
            stored = await _redis.StringGetAsync(AdminSessionRevocation.Key(userId));
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "读取账号 {UserId} 的会话吊销状态失败，按拒绝处理", userId);
            return new RevocationResult(false, true);
        }

        if (stored.IsNullOrEmpty) return new RevocationResult(false, false);

        if (!long.TryParse(stored.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var revokedAt))
        {
            // 键存在但值不是时间戳：说明有人写坏了数据。同样按不可用处理，不要猜。
            _logger.LogError(
                "账号 {UserId} 的会话吊销键值不是合法时间戳：{Value}", userId, stored.ToString());
            return new RevocationResult(false, true);
        }

        return new RevocationResult(issuedAtUnixSeconds.Value <= revokedAt, false);
    }
}
