namespace Collaboration.Domain.Configuration;

/// <summary>
/// 配置源抽象：服务在连接任何依赖之前，先从这里拿到全部业务配置。
/// </summary>
/// <remarks>
/// 链路位置：DATA_SPEC.md 1.2 的 S0~S3 阶段。
/// 为什么是抽象而不是写死 AgileConfig：正式方案是 AgileConfig，但配置源本身是可替换的
/// （BootstrapOptions.Source 决定用哪个实现），这样换源不需要动任何业务代码。
/// 约束：实现**不得**在返回值里塞默认值。缺项就是缺项，由调用方 fail-fast 处理
/// （DATA_SPEC 1.1 铁律 2：不做默认值兜底）。
/// </remarks>
public interface IConfigSource
{
    /// <summary>
    /// 来源标识，用于启动日志与排查，例如 AgileConfig、LocalFile。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 拉取本服务的全部配置。
    /// </summary>
    /// <param name="ct">取消令牌。取消时调用方应视为配置未取到并退出。</param>
    /// <returns>
    /// 扁平化的配置键值对，键用 .NET 配置节语法，例如 "ConnectionStrings:Default"、"Redis:Database"。
    /// </returns>
    /// <exception cref="ConfigSourceUnavailableException">配置源不可达或未配置完成。</exception>
    Task<Dictionary<string, string?>> LoadAsync(CancellationToken ct = default);
}

/// <summary>
/// 配置源不可用异常。调用方必须据此 fail-fast，不得降级启动。
/// </summary>
public sealed class ConfigSourceUnavailableException : Exception
{
    /// <summary>用给定原因构造。</summary>
    /// <param name="message">失败原因，需包含源名称与最后一次错误。</param>
    public ConfigSourceUnavailableException(string message) : base(message)
    {
    }

    /// <summary>用给定原因与内部异常构造。</summary>
    /// <param name="message">失败原因。</param>
    /// <param name="innerException">最后一次失败捕获的异常。</param>
    public ConfigSourceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

