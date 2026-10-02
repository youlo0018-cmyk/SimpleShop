namespace Collaboration.Domain.Configuration;

/// <summary>
/// 引导配置：服务在连接任何依赖之前必须先知道去哪里取配置。
/// </summary>
/// <remarks>
/// 链路位置：DATA_SPEC.md 1.2 的 S0 阶段，读取来源是本地 appsettings 或环境变量。
/// 本类只描述「去哪里取配置」，不含连接串等业务配置——连接串一律由配置源下发
/// （DATA_SPEC 1.1 铁律 1）。
/// </remarks>
public sealed class BootstrapOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "Bootstrap";

    /// <summary>
    /// 配置源类型。AgileConfig 为正式方案，LocalFile 为本地开发过渡方案。
    /// </summary>
    public string Source { get; set; } = "LocalFile";

    /// <summary>AgileConfig 注册中心地址，例如 http://127.0.0.1:5000。</summary>
    public string AgileConfigAddress { get; set; } = string.Empty;

    /// <summary>AgileConfig 应用 Id，按约定等于服务名（DATA_SPEC 1.1 铁律 3）。</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>AgileConfig 访问密钥。</summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>
    /// AgileConfig 的环境名，例如 DEV、TEST、PROD。
    /// </summary>
    /// <remarks>
    /// 刻意与 ASP.NET 的 EnvironmentName 分开：两者可以合法地不同
    /// （例如 ASP.NET 用 Development，配置中心用 DEV）。留空时回退到 ASP.NET 环境名。
    /// </remarks>
    public string Env { get; set; } = string.Empty;

    /// <summary>
    /// 连接配置中心的重试次数，失败按指数退避重试。
    /// </summary>
    /// <remarks>全部失败则 fail-fast 退出（DATA_SPEC 1.1 铁律 2）。</remarks>
    public int MaxRetryCount { get; set; } = 5;
}

