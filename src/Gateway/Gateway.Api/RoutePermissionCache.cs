using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Options;

namespace Gateway.Api;

/// <summary>一次路径解析的结果。</summary>
/// <param name="Available">映射是否可用（权限中心正常）。false 时应按配置决定放行还是拒绝。</param>
/// <param name="RequiredCode">该路径需要的权限点编码；不需要时为 null。</param>
/// <remarks>
/// 刻意用返回值而不是 out 参数：async 方法不允许 out / ref 参数（C# 编译器直接报错）。
/// </remarks>
public readonly record struct RoutePermissionLookup(bool Available, string? RequiredCode);

/// <summary>网关 RBAC 的「路径 → 权限点」映射缓存。</summary>
/// <remarks>
/// 映射从权限中心拉，而不是写死在路由表里——权限点是运行时可维护的实体，
/// 新增权限点、改 ApiPath 都要能「立即生效于网关」（BUSINESS 5.4）。
/// 缓存 30 秒（BUSINESS 5.3），是为了不让每个请求都打一次权限中心。
///
/// 拉取失败时按 <see cref="RbacOptions.AllowAllWhenUnavailable"/> 决定放行还是拒绝。
/// 默认拒绝：权限中心挂掉就全站 403，比「挂掉时人人都是超管」安全。
/// </remarks>
public sealed class RoutePermissionCache : IDisposable
{
    private readonly HttpClient _http;
    private readonly RbacOptions _options;
    private readonly ILogger<RoutePermissionCache> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private volatile IReadOnlyDictionary<string, string> _map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private volatile bool _unavailable;

    /// <summary>构造缓存。</summary>
    /// <param name="http">指向权限中心的 HttpClient。</param>
    /// <param name="options">网关配置。</param>
    /// <param name="logger">日志器。</param>
    public RoutePermissionCache(HttpClient http, IOptions<GatewayOptions> options, ILogger<RoutePermissionCache> logger)
    {
        _http = http;
        _options = options.Value.Rbac;
        _logger = logger;
    }

    /// <summary>权限中心当前是否不可用。</summary>
    public bool Unavailable => _unavailable;

    /// <summary>取路径对应的权限点编码。</summary>
    /// <param name="path">网关侧请求路径（不含查询串）。</param>
    /// <returns>解析结果。</returns>
    public async Task<RoutePermissionLookup> ResolveAsync(string path)
    {
        await EnsureLoadedAsync();

        if (_unavailable) return new RoutePermissionLookup(false, null);

        if (_map.TryGetValue(path, out var exact))
        {
            return new RoutePermissionLookup(true, exact);
        }

        // 🔴 前缀匹配。必须挑**最长**的命中项，不能「第一个匹配的就返回」。
        // Dictionary 的遍历顺序不保证稳定，两个长度不同的通配前缀都命中时
        // （如 /gateway/reports/* 与 /gateway/reports/Point/*）挑到哪个是随机的——
        // 症状是「这个接口到底要什么权限」变成薛定谔的，同一个人时而被放行时而被拒。
        // 与权限中心 GetRouteMapHandler「同一路径只留一个权限点」是同一个道理。
        string? bestCode = null;
        var bestLength = -1;

        foreach (var (key, value) in _map)
        {
            // 通配：以 /* 结尾表示「该前缀及其下所有子路径」。
            // 之前没处理这个分支：/gateway/logs/* 被当成字面量，前缀算成
            // /gateway/logs/*/，于是永远匹配不上 /gateway/logs/Pv/List，
            // 结果 requiredCode 为 null、请求被**放行**——
            // log:read / report:view / permission:manage / file:upload
            // 这几个权限点等于完全没生效（任何登录用户都能读日志、改权限点）。
            if (key.EndsWith("/*", StringComparison.Ordinal))
            {
                var basePath = key[..^2];
                var hit = path.Equals(basePath, StringComparison.OrdinalIgnoreCase)
                          || path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase);

                if (hit && basePath.Length > bestLength)
                {
                    bestCode = value;
                    bestLength = basePath.Length;
                }

                continue;
            }

            // 普通前缀：允许 /gateway/files/Content/{对象键} 这类带路径参数的接口
            // 绑定到 /gateway/files/Content 或 /gateway/files/Content/。
            var prefix = key.EndsWith('/') ? key : key + "/";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (prefix.Length > bestLength)
                {
                    bestCode = value;
                    bestLength = prefix.Length;
                }
            }
        }

        if (bestCode is not null) return new RoutePermissionLookup(true, bestCode);

        return new RoutePermissionLookup(true, null);
    }

    /// <summary>强制下次读取时重新拉取。权限点增删改后应调用。</summary>
    public void Invalidate() => _loadedAt = DateTimeOffset.MinValue;

    private async Task EnsureLoadedAsync()
    {
        var age = DateTimeOffset.UtcNow - _loadedAt;
        if (_loadedAt != DateTimeOffset.MinValue && age < TimeSpan.FromSeconds(_options.CacheSeconds)) return;

        await _refreshLock.WaitAsync();
        try
        {
            age = DateTimeOffset.UtcNow - _loadedAt;
            if (_loadedAt != DateTimeOffset.MinValue && age < TimeSpan.FromSeconds(_options.CacheSeconds)) return;

            var response = await _http.GetAsync("internal/permissions/RouteMap");
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<RouteMapEntry>>>();
            if (body is null || !body.Success || body.Data is null)
            {
                throw new InvalidOperationException($"权限中心返回业务失败: {body?.Message ?? "(空响应)"}");
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in body.Data)
            {
                if (!string.IsNullOrWhiteSpace(entry.Path) && !string.IsNullOrWhiteSpace(entry.Code))
                {
                    map[entry.Path.Trim()] = entry.Code.Trim();
                }
            }

            _map = map;
            _unavailable = false;
            _loadedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("已刷新网关 RBAC 映射，共 {Count} 条路径", map.Count);
        }
        catch (Exception ex)
        {
            _unavailable = true;
            _logger.LogError(ex,
                "拉取网关 RBAC 映射失败，当前按 {Mode} 处理",
                _options.AllowAllWhenUnavailable ? "放行" : "拒绝");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>释放信号量。</summary>
    public void Dispose() => _refreshLock.Dispose();

    private sealed record RouteMapEntry(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("code")] string Code);
}
