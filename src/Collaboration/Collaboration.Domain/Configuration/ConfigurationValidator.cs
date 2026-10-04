namespace Collaboration.Domain.Configuration;

/// <summary>配置完整性校验，S3 阶段用，缺项直接失败（DATA_SPEC 1.2）。</summary>
public static class ConfigurationValidator
{
    /// <summary>所有服务都必须具备的配置键，服务可追加但不能减少。</summary>
    public static IReadOnlyList<string> BaseRequiredKeys { get; } = BuildBaseKeys();

    /// <summary>
    /// 豁免的基础键名，供**确实不需要**该资源的服务显式声明。
    /// </summary>
    /// <remarks>
    /// 目前只有 Gateway 用了它：网关不连数据库、不发消息，
    /// 让它填一个永远用不上的 ConnectionStrings:Default 属于撒谎配置——
    /// 将来有人排查时看到「网关有数据库连接串」会以为它真的连了库。
    /// 显式豁免比填假值诚实。
    /// </remarks>
    public const string DatabaseConnectionKey = "ConnectionStrings:Default";

    /// <summary>Redis 连接串键名。</summary>
    /// <remarks>
    /// 和 <see cref="DatabaseConnectionKey"/> 同理：确实不连 Redis 的服务显式豁免它。
    /// LogService 就是这种——它的数据全在 Elasticsearch，Redis 派不上用场，
    /// 为了满足基础校验而硬连一次，只会多一个「它到底用 Redis 干什么」的疑问。
    /// </remarks>
    public const string RedisConnectionKey = "Redis:ConnectionString";

    /// <summary>校验配置是否齐全，缺项则抛出并列出全部缺失键。</summary>
    /// <param name="config">已拉取的配置键值对。</param>
    /// <param name="extra">服务追加的必填键，可为空。</param>
    /// <param name="exemptBaseKeys">要从基础必填项里豁免的键，必须显式列出，不做「全部豁免」这种宽松开关。</param>
    /// <exception cref="ConfigSourceUnavailableException">存在缺失键时抛出。</exception>
    public static void EnsureRequired(
        IReadOnlyDictionary<string, string?> config,
        IReadOnlyList<string>? extra = null,
        IReadOnlyList<string>? exemptBaseKeys = null)
    {
        // 自己建一份大小写不敏感的视图：配置键按 .NET 约定应大小写不敏感，
        // 但调用方传进来的字典 comparer 不受我们控制，不能假设它已经正确。
        var lookup = new Dictionary<string, string?>(config, StringComparer.OrdinalIgnoreCase);
        var exempt = new HashSet<string>(exemptBaseKeys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in BaseRequiredKeys.Concat(extra ?? Array.Empty<string>()))
        {
            if (!seen.Add(key)) continue;
            if (exempt.Contains(key)) continue;
            if (!lookup.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) missing.Add(key);
        }

        if (missing.Count > 0)
        {
            throw new ConfigSourceUnavailableException("配置缺失以下必需项，服务拒绝启动：" + string.Join(", ", missing));
        }
    }

    private static IReadOnlyList<string> BuildBaseKeys()
    {
        var keys = new List<string>();
        keys.Add(DatabaseConnectionKey);
        keys.Add("Redis:ConnectionString");
        keys.Add("Consul:Address");
        keys.Add("RabbitMq:Host");
        return keys;
    }
}
