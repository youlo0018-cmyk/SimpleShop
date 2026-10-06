using Collaboration.Domain.Security;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace UserService.Application.Services;

/// <summary>走 Redis 的会话吊销实现：写入「该账号的吊销时刻」。</summary>
/// <remarks>
/// 写在共享库（<c>Redis:SharedDatabase</c>，默认 0），因为读这条键的是网关，
/// 而网关在它自己的库上。写错库的表现是「接口返回成功、旧令牌照样能用」，
/// 属于最难查的一类静默失败，所以库号必须与网关同源。
/// </remarks>
public sealed class RedisAdminSessionRevoker : IAdminSessionRevoker
{
    private readonly IConnectionMultiplexer _redis;
    private readonly int _sharedDatabase;
    private readonly ILogger<RedisAdminSessionRevoker> _logger;

    /// <summary>构造吊销器。</summary>
    /// <param name="redis">Redis 连接。</param>
    /// <param name="sharedDatabase">共享逻辑库索引。</param>
    /// <param name="logger">日志器。</param>
    public RedisAdminSessionRevoker(
        IConnectionMultiplexer redis, int sharedDatabase, ILogger<RedisAdminSessionRevoker> logger)
    {
        _redis = redis;
        _sharedDatabase = sharedDatabase;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 写失败时**向上抛**，不吞掉：调用方（重置密码）如果连吊销都没做成，
    /// 就必须让操作者知道「密码改了但旧令牌还活着」，而不是回一句「密码已重置」。
    /// </remarks>
    public async Task RevokeAsync(long userId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase(_sharedDatabase);
        var revokedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await db.StringSetAsync(
            AdminSessionRevocation.Key(userId),
            revokedAt,
            TimeSpan.FromSeconds(AdminSessionRevocation.KeyTtlSeconds));

        _logger.LogInformation(
            "账号 {UserId} 的会话已吊销，早于 {RevokedAt} 签发的后台令牌全部失效", userId, revokedAt);
    }
}
