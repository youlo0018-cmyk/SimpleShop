using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Gateway.Api;

/// <summary>网关依赖健康检查。</summary>
/// <remarks>
/// <para>之前 <c>/health</c> 只挂了一个恒为 Healthy 的 self 检查，
/// 于是权限中心挂了它照样回 Healthy。后果是编排系统继续把流量打进来，
/// 而网关此时<b>每一个需要鉴权的请求都会被拒</b>——
/// 一个「活着但什么都干不了」的网关，探针却告诉运维它很健康。</para>
///
/// <para><b>只探网关自己真正依赖的东西</b>，不多探也不少探：
/// <list type="bullet">
/// <item><b>权限中心</b>：RBAC 默认 fail-closed（拉不到映射就全拒），
/// 它一挂网关对业务而言立刻不可用，是最该被发现的一个。</item>
/// <item><b>路由表</b>：<c>ocelot.json</c> 加载失败时网关一个请求都转不出去，
/// 而这种故障在启动日志里很容易被淹掉。</item>
/// <item><b>Redis</b>：后台令牌的会话吊销状态存在这里，读不到就 fail-closed 拒绝请求
/// （DATA_SPEC 5.20）。它一挂，后台整体不可用，所以必须进就绪探针。</item>
/// </list>
/// </para>
///
/// <para><b>刻意不探数据库</b>：网关没有数据库连接
/// （见 <c>Program.cs</c> 对 DatabaseConnectionKey 的豁免）。
/// 探一个本进程压根不用的依赖，拿到的是与网关健康无关的信号，
/// 反而会在数据库抖动时把网关实例踢下线。
/// 同样不探 Consul：<c>ocelot.json</c> 走的是显式 <c>DownstreamHostAndPorts</c>，
/// 服务发现并没有真的接上，探它只会得到一个与转发能力无关的结果。</para>
/// </remarks>
public sealed class GatewayHealthCheck : IHealthCheck
{
    /// <summary>
    /// 启动宽限期：进程起来之后的这段时间内，依赖不通只算 Degraded 不算 Unhealthy。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须有它</b>：启动脚本是<b>串行</b>拉服务的，网关排在权限中心前面。
    /// 刚起来的头几秒里权限中心必然还没监听，此时若无条件报 Unhealthy，
    /// 启动脚本会判定「网关起不来」而一直等，最后把一个完全正常的网关误杀。
    ///
    /// 这就是 K8s 里 startupProbe 与 livenessProbe 分开的同一个道理：
    /// 「还没启动完」和「启动后坏了」要用两套判定，否则慢启动的服务永远活不下来。
    /// 宽限期过了之后依赖仍不通，就该如实报 Unhealthy。
    /// </remarks>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(90);

    private static readonly DateTimeOffset ProcessStart = DateTimeOffset.UtcNow;

    private readonly IHttpClientFactory _http;
    private readonly IOptions<GatewayOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly StackExchange.Redis.IConnectionMultiplexer _redis;

    /// <summary>构造健康检查。</summary>
    /// <param name="http">HTTP 客户端工厂。</param>
    /// <param name="options">网关配置（RBAC 一节在它下面）。</param>
    /// <param name="configuration">应用配置（<c>ocelot.json</c> 也由它加载）。</param>
    /// <param name="redis">Redis 连接，用于探会话吊销存储。</param>
    public GatewayHealthCheck(
        IHttpClientFactory http,
        IOptions<GatewayOptions> options,
        IConfiguration configuration,
        StackExchange.Redis.IConnectionMultiplexer redis)
    {
        _http = http;
        _options = options;
        _configuration = configuration;
        _redis = redis;
    }

    /// <summary>执行检查。</summary>
    /// <param name="context">检查上下文。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>聚合后的健康结果。</returns>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>();

        var routes = CheckRoutes();
        data[routes.Key] = routes.Value;

        var permission = await CheckPermissionServiceAsync(ct).ConfigureAwait(false);
        data[permission.Key] = permission.Value;

        var sessionStore = await CheckSessionStoreAsync(ct).ConfigureAwait(false);
        data[sessionStore.Key] = sessionStore.Value;

        var unhealthy = data
            .Where(a => a.Value.ToString()!.StartsWith("Unhealthy", StringComparison.Ordinal))
            .Select(a => a.Key)
            .ToArray();

        if (unhealthy.Length == 0)
        {
            return HealthCheckResult.Healthy("网关依赖正常", data);
        }

        // 宽限期内只降级不判死：让串行启动能顺利把依赖拉起来，
        // 但依赖状态照样写在 data 里，谁来看都能看见「它现在还没起来」
        var elapsed = DateTimeOffset.UtcNow - ProcessStart;
        if (elapsed < StartupGrace)
        {
            return HealthCheckResult.Degraded(
                $"启动宽限期内（{elapsed.TotalSeconds:F0}s / {StartupGrace.TotalSeconds:F0}s），"
                + $"依赖尚未就绪：{string.Join("、", unhealthy)}",
                data: data);
        }

        return HealthCheckResult.Unhealthy(
            $"依赖异常：{string.Join("、", unhealthy)}", data: data);
    }

    /// <summary>检查路由表是否真的加载进来了。</summary>
    /// <returns>状态文本。</returns>
    /// <remarks>
    /// 路由数为 0 是个很隐蔽的故障：进程正常、端口正常、健康检查也正常，
    /// 但所有请求都 404。单独把它列出来能让这类问题一眼可见。
    /// </remarks>
    private KeyValuePair<string, string> CheckRoutes()
    {
        // 直接读配置而不是 Ocelot 的内部配置对象：
        // ocelot.json 本来就是用 AddJsonFile 加进来的，这里读的是同一份数据，
        // 却不会把网关焊死在 Ocelot 的某个内部类型上（换个版本就编译不过）。
        var count = _configuration.GetSection("Routes").GetChildren().Count();
        return count > 0
            ? new KeyValuePair<string, string>("Routes", $"Healthy: {count} 条路由已加载")
            : new KeyValuePair<string, string>("Routes", "Unhealthy: 路由表为空，所有请求都会 404");
    }

    /// <summary>探 Redis（会话吊销存储）是否可达。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>状态文本。</returns>
    /// <remarks>
    /// 只发 PING，不读具体键：要回答的问题是「这个依赖还活着吗」，
    /// 而不是「某个账号被吊销了没有」。
    /// </remarks>
    private async Task<KeyValuePair<string, string>> CheckSessionStoreAsync(CancellationToken ct)
    {
        var sharedDatabase = _configuration.GetSection(
            Collaboration.Domain.Configuration.RedisOptions.SectionName)
            .Get<Collaboration.Domain.Configuration.RedisOptions>()?.SharedDatabase ?? 0;

        var sw = Stopwatch.StartNew();
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(3));

            await _redis.GetDatabase(sharedDatabase).PingAsync().WaitAsync(probe.Token).ConfigureAwait(false);
            sw.Stop();

            return new KeyValuePair<string, string>("SessionStore", $"Healthy: {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            return new KeyValuePair<string, string>("SessionStore", $"Unhealthy: {ex.Message}");
        }
    }

    /// <summary>探权限中心能否拉到权限树。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>状态文本。</returns>
    private async Task<KeyValuePair<string, string>> CheckPermissionServiceAsync(CancellationToken ct)
    {
        // ⚠️ 必须从 GatewayOptions 里取 `.Rbac`：只注册了 GatewayOptions，
        // 直接注入 IOptions<RbacOptions> 拿到的是一个**没人配置过的空实例**，
        // 于是健康检查会永远报「未配置 PermissionServiceUrl」——
        // 看起来像配置丢了，其实是自己的注入方式错了。
        var url = _options.Value.Rbac.PermissionServiceUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return new KeyValuePair<string, string>(
                "PermissionService", "Unhealthy: 未配置 PermissionServiceUrl");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            // 3 秒上限：探活接口本身卡住会把编排系统的健康检查一起拖死，
            // 那比探活失败更糟。
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(3));

            var client = _http.CreateClient("GatewayHealthProbe");
            using var response = await client
                .GetAsync(new Uri(new Uri(url.TrimEnd('/') + "/"), "permissions/Tree"), probe.Token)
                .ConfigureAwait(false);
            sw.Stop();

            return response.IsSuccessStatusCode
                ? new KeyValuePair<string, string>(
                    "PermissionService", $"Healthy: {sw.ElapsedMilliseconds}ms")
                : new KeyValuePair<string, string>(
                    "PermissionService", $"Unhealthy: HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new KeyValuePair<string, string>("PermissionService", $"Unhealthy: {ex.Message}");
        }
    }
}
