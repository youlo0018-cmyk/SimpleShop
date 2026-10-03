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
    /// 分配雪花 workerId（S4）：抢占一个带 TTL 的空闲槽位，并后台续租。
    /// </summary>
    /// <param name="redis">Redis 连接。</param>
    /// <param name="keyPrefix">key 前缀，最终 key 为 {前缀}:{服务名}。</param>
    /// <param name="appName">服务名。</param>
    /// <param name="upperBound">槽位总数（不含上界），即 workerId 可用范围。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分配到的 workerId。</returns>
    /// <remarks>
    /// 租约由 <see cref="WorkerIdLease"/> 的静态定时器持有，不会被 GC 回收，
    /// 所以这里<b>不返回租约对象</b>——12 个 Program.cs 的调用点保持一致。
    /// 进程退出时来不及释放租约也没关系：90 秒后 key 自动过期，槽位回到池子里。
    /// </remarks>
    public static async Task<ushort> AllocateWorkerIdAsync(
        IDatabase redis,
        string keyPrefix,
        string appName,
        ushort upperBound,
        CancellationToken ct = default)
    {
        var lease = await WorkerIdLease.AcquireAsync(redis, keyPrefix, appName, upperBound, ct);
        Console.WriteLine($"[bootstrap] {appName} 分配 workerId={lease.WorkerId}（租约 key={lease.Key}，90 秒自动续租）");
        return lease.WorkerId;
    }
}

