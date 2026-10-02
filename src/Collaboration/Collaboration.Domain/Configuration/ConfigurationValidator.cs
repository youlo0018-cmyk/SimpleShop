namespace Collaboration.Domain.Configuration;

/// <summary>配置完整性校验，S3 阶段用，缺项直接失败（DATA_SPEC 1.2）。</summary>
public static class ConfigurationValidator
{
    /// <summary>所有服务都必须具备的配置键，服务可追加但不能减少。</summary>
    public static IReadOnlyList<string> BaseRequiredKeys { get; } = BuildBaseKeys();

    /// <summary>校验配置是否齐全，缺项则抛出并列出全部缺失键。</summary>
    /// <param name="config">已拉取的配置键值对。</param>
    /// <param name="extra">服务追加的必填键，可为空。</param>
    /// <exception cref="ConfigSourceUnavailableException">存在缺失键时抛出。</exception>
    public static void EnsureRequired(IReadOnlyDictionary<string, string?> config, IReadOnlyList<string>? extra = null)
    {
        // 自己建一份大小写不敏感的视图：配置键按 .NET 约定应大小写不敏感，
        // 但调用方传进来的字典 comparer 不受我们控制，不能假设它已经正确。
        var lookup = new Dictionary<string, string?>(config, StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in BaseRequiredKeys.Concat(extra ?? Array.Empty<string>()))
        {
            if (!seen.Add(key)) continue;
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
        keys.Add("ConnectionStrings:Default");
        keys.Add("Redis:ConnectionString");
        keys.Add("Consul:Address");
        keys.Add("RabbitMq:Host");
        return keys;
    }
}

