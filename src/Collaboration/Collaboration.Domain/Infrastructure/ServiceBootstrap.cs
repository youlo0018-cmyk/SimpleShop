using Collaboration.Domain.Configuration;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Collaboration.Domain.Infrastructure;

/// <summary>服务启动时序的 S0~S4 阶段（DATA_SPEC 1.2）。</summary>
public static partial class ServiceBootstrap
{
    /// <summary>
    /// 从配置源拉取配置并校验完整性，返回可直接绑定到 Options 的键值对。
    /// </summary>
    /// <param name="bootstrapConfig">引导配置，只含 Bootstrap 节与环境变量。</param>
    /// <param name="appName">服务名，同时用于日志与配置源 appId。</param>
    /// <param name="environment">环境名，写入日志便于排查。</param>
    /// <param name="extraRequiredKeys">服务追加的必填配置键。</param>
    /// <param name="exemptBaseKeys">要从基础必填项里豁免的键（见 ConfigurationValidator），可为空。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>校验通过的配置键值对。</returns>
    /// <exception cref="ConfigSourceUnavailableException">配置源不可达或配置缺项。</exception>
    public static async Task<Dictionary<string, string?>> LoadConfigurationAsync(
        IConfiguration bootstrapConfig,
        string appName,
        string environment,
        IReadOnlyList<string>? extraRequiredKeys,
        IReadOnlyList<string>? exemptBaseKeys = null,
        CancellationToken ct = default)
    {
        var bootstrap = bootstrapConfig.GetSection(BootstrapOptions.SectionName).Get<BootstrapOptions>()
            ?? new BootstrapOptions();

        var source = CreateSource(bootstrap, bootstrapConfig, environment);
        Console.WriteLine($"[bootstrap] {appName} ({environment}) 配置源: {source.Name}");

        Dictionary<string, string?> config;
        try
        {
            config = await RetryAsync(() => source.LoadAsync(ct), bootstrap.MaxRetryCount, source.Name, ct);
        }
        catch (ConfigSourceUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ConfigSourceUnavailableException($"从配置源 {source.Name} 拉取配置失败。", ex);
        }

        ConfigurationValidator.EnsureRequired(config, extraRequiredKeys, exemptBaseKeys);
        return config;
    }

    /// <summary>
    /// 用 Redis INCR 原子自增分配雪花 workerId（S4）。
    /// </summary>
    /// <param name="redis">Redis 连接。</param>
    /// <param name="keyPrefix">key 前缀，最终 key 为 {前缀}:{服务名}。</param>
    /// <param name="appName">服务名。</param>
    /// <param name="upperBound">workerId 上限（不含），超过则失败且不回收。</param>
    /// <returns>分配到的 workerId。</returns>
    public static async Task<ushort> AllocateWorkerIdAsync(
        IDatabase redis,
        string keyPrefix,
        string appName,
        ushort upperBound)
    {
        var key = $"{keyPrefix}:{appName}";
        var value = await redis.StringIncrementAsync(key);
        Console.WriteLine($"[bootstrap] {appName} 分配 workerId={value}（key={key}）");

        if (value > upperBound)
        {
            throw new InvalidOperationException(
                $"雪花 workerId 超出上限：已分配到 {value}，上限 {upperBound}（不含）。不做回绕复用，请检查 {key}。");
        }

        return (ushort)value;
    }
}

