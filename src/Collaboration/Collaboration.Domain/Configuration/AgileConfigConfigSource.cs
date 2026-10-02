using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Collaboration.Domain.Configuration;

/// <summary>AgileConfig 配置源：从配置中心拉取本服务已发布的配置。</summary>
/// <remarks>
/// 契约（源码 dotnetcore/AgileConfig v1.13.3）：GET {address}/api/config/app/{appId}?env={env}，
/// HTTP Basic 认证（用户名 appId、密码 secret），返回 [{group,key,value}]，key 已是 .NET 配置节语法。
/// 只返回已发布配置；读取失败必须抛出，由调用方 fail-fast（DATA_SPEC 1.1 铁律 2）。
/// </remarks>
public sealed class AgileConfigConfigSource : IConfigSource
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _appId;
    private readonly string _secret;
    private readonly string _env;

    /// <summary>构造配置源。</summary>
    /// <param name="http">HttpClient，由 DI 注入复用，不要每次请求新建。</param>
    /// <param name="appId">应用 Id，按约定等于服务名。</param>
    /// <param name="secret">应用密钥。</param>
    /// <param name="env">环境名，例如 DEV。</param>
    public AgileConfigConfigSource(HttpClient http, string appId, string secret, string env)
    {
        _http = http;
        _appId = appId;
        _secret = secret;
        _env = env;
    }

    /// <inheritdoc />
    public string Name => "AgileConfig";

    /// <summary>拉取并扁平化已发布配置。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>键值对，键为 .NET 配置节语法。</returns>
    /// <exception cref="ConfigSourceUnavailableException">网络失败、认证失败或返回为空。</exception>
    public async Task<Dictionary<string, string?>> LoadAsync(CancellationToken ct = default)
    {
        var url = $"{_http.BaseAddress}api/config/app/{_appId}?env={_env}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_appId}:{_secret}")));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ConfigSourceUnavailableException($"连接 AgileConfig 失败：{url}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ConfigSourceUnavailableException(
                    $"AgileConfig 返回 {(int)response.StatusCode}：{url}。401/403 是 appId 或 secret 不对，404 是应用未启用或环境名不对。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var items = await JsonSerializer.DeserializeAsync<List<Item>>(stream, JsonOpts, ct) ?? new List<Item>();

            if (items.Count == 0)
            {
                throw new ConfigSourceUnavailableException(
                    $"AgileConfig 应用 {_appId} 在环境 {_env} 下没有已发布的配置，先执行 scripts/seed-agileconfig.ps1。");
            }

            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Key)) continue;
                var key = item.Key.Contains(':') ? item.Key : $"{item.Group}:{item.Key}";
                result[key] = item.Value;
            }

            return result;
        }
    }

    private sealed class Item
    {
        /// <summary>配置分组，通常等于 .NET 配置节名。</summary>
        public string? Group { get; set; }

        /// <summary>配置键，已是 .NET 配置节语法。</summary>
        public string? Key { get; set; }

        /// <summary>配置值。</summary>
        public string? Value { get; set; }
    }
}

