using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace UserService.Application.Services;

/// <summary>走内网 HTTP 调商户平台服务取显示名。</summary>
/// <remarks>
/// <b>失败只降级不报错</b>：名称是展示字段，取不到不该让账号列表整个打不开。
/// 取不到时结果里缺项，调用方回落成 Id 字符串（信息不全但至少能对上数据）。
/// </remarks>
public sealed class HttpPlatformNameClient : IPlatformNameClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpPlatformNameClient> _logger;

    /// <summary>构造客户端。</summary>
    /// <param name="http">指向商户平台服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpPlatformNameClient(HttpClient http, ILogger<HttpPlatformNameClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<NameLookup> GetNamesAsync(
        IReadOnlyCollection<long> platformIds,
        IReadOnlyCollection<long> merchantIds,
        CancellationToken ct = default)
    {
        var empty = new NameLookup(
            new Dictionary<long, string>(), new Dictionary<long, string>());

        if (platformIds.Count == 0 && merchantIds.Count == 0) return empty;

        try
        {
            var response = await _http.PostAsJsonAsync(
                "internal/platforms/Names",
                new NameLookupRequest(platformIds.ToArray(), merchantIds.ToArray()),
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("取平台 / 商户名称失败：HTTP {Code}，列表将回落显示 Id", (int)response.StatusCode);
                return empty;
            }

            var body = await response.Content
                .ReadFromJsonAsync<NameLookupResponse>(ct).ConfigureAwait(false);

            if (body?.Data is null)
            {
                _logger.LogWarning("取平台 / 商户名称返回空响应，列表将回落显示 Id");
                return empty;
            }

            return new NameLookup(
                body.Data.Platforms.ToDictionary(a => a.Id, a => a.Name),
                body.Data.Merchants.ToDictionary(a => a.Id, a => a.Name));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "取平台 / 商户名称调用异常，列表将回落显示 Id");
            return empty;
        }
    }

    /// <summary>请求体。</summary>
    private sealed record NameLookupRequest(
        [property: JsonPropertyName("platformIds")] long[] PlatformIds,
        [property: JsonPropertyName("merchantIds")] long[] MerchantIds);

    /// <summary>响应信封。</summary>
    private sealed record NameLookupResponse(
        [property: JsonPropertyName("data")] NameLookupData? Data);

    /// <summary>响应数据。</summary>
    private sealed record NameLookupData(
        [property: JsonPropertyName("platforms")] List<NamePair> Platforms,
        [property: JsonPropertyName("merchants")] List<NamePair> Merchants);

    /// <summary>一个 Id → 名称。</summary>
    private sealed record NamePair(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name);
}
