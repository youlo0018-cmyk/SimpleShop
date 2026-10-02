using Microsoft.Extensions.Configuration;

namespace Collaboration.Domain.Configuration;

/// <summary>
/// 本地文件配置源：读 appsettings.{Environment}.json。
/// </summary>
/// <remarks>
/// 定位：AgileConfig 网络不可达时的过渡方案（PLAN.md 2.6 选项 a）。
/// 它**只改变配置来源**，不改变「缺项即失败」的语义——没有给任何配置项提供默认值，
/// 所以不会掩盖配置缺失，接缝仍然是 fail-fast。
/// 注意：Bootstrap 节是引导配置（描述去哪里取配置），不属于业务配置，导出时排除。
/// </remarks>
public sealed class LocalFileConfigSource : IConfigSource
{
    private readonly IConfiguration _configuration;
    private readonly string _environment;

    /// <summary>
    /// 用引导阶段已加载的配置构造。
    /// </summary>
    /// <param name="configuration">引导配置，含 appsettings 与环境变量。</param>
    /// <param name="environment">环境名，用于日志，例如 Development。仅写进日志，不参与取值。</param>
    public LocalFileConfigSource(IConfiguration configuration, string environment)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _environment = environment;
    }

    /// <inheritdoc />
    public string Name => "LocalFile";

    /// <summary>
    /// 导出全部业务配置。
    /// </summary>
    /// <param name="ct">本实现为同步内存读取，取消令牌仅用于接口契约对齐。</param>
    /// <returns>扁平化键值对，已排除 Bootstrap 节。</returns>
    public Task<Dictionary<string, string?>> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in _configuration.AsEnumerable())
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (key.StartsWith(BootstrapOptions.SectionName, StringComparison.OrdinalIgnoreCase)) continue;
            result[key] = value;
        }

        if (result.Count == 0)
        {
            throw new ConfigSourceUnavailableException(
                $"配置源 LocalFile（环境 {_environment}）没有读到任何业务配置。" +
                "appsettings 中除 Bootstrap 外至少要有 ConnectionStrings、Redis 等节。");
        }

        return Task.FromResult(result);
    }
}

