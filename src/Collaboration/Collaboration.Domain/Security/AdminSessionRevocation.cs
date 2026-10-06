namespace Collaboration.Domain.Security;

/// <summary>
/// 后台账号的会话吊销约定（DATA_SPEC 5.20）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：后台 access token 是自包含的，签发后服务端不再查库，
/// 所以「重置密码」只改哈希的话，旧令牌照样有效到过期（默认 2 小时）。
/// 而重置密码的真实动机往往正是「怀疑账号被盗」，此时最需要的是立刻把攻击者踢出去。</para>
///
/// <para><b>机制</b>：UserService 在重置密码时往 Redis 写一条「该账号的吊销时刻」，
/// 网关在每个后台请求上比对令牌的签发时间（<c>iat</c>）：早于吊销时刻的一律拒绝。
/// 这里存**时刻**而不是布尔值：布尔值只能表达「踢一次」，
/// 而且没法区分「吊销前签发的令牌」与「吊销后重新登录拿到的令牌」。</para>
///
/// <para><b>键为什么放共享库</b>：写入方是 UserService、读取方是网关，两边库号不同就永远读不到，
/// 而且失败是静默的。见 <c>RedisOptions.SharedDatabase</c>。</para>
/// </remarks>
public static class AdminSessionRevocation
{
    /// <summary>吊销键前缀。完整键形如 <c>auth:revoked:1234567890</c>。</summary>
    public const string KeyPrefix = "auth:revoked:";

    /// <summary>
    /// 键的存活秒数。
    /// </summary>
    /// <remarks>
    /// 后台 access token 有效期 2 小时（BUSINESS 4.1），这里给 24 小时余量。
    /// 比令牌活得久就够了：吊销时刻之后再签发的令牌本来就不受影响，
    /// 而吊销时刻之前签发的令牌早已自然过期。留太长只会白占内存。
    /// </remarks>
    public const int KeyTtlSeconds = 24 * 60 * 60;

    /// <summary>构造某账号的吊销键。</summary>
    /// <param name="userId">后台账号 Id。</param>
    /// <returns>Redis 键名。</returns>
    public static string Key(long userId) => KeyPrefix + userId;
}
