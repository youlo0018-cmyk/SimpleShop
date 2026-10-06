using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;
using OrderService.Domain.Ports;

namespace OrderService.Infrastructure.Ports;

/// <summary>走内网 HTTP 调商户平台服务，取平台级运费配置。</summary>
public sealed class HttpPlatformPort : IPlatformPort
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpPlatformPort> _logger;

    /// <summary>构造端口。</summary>
    /// <param name="http">指向商户平台服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpPlatformPort(HttpClient http, ILogger<HttpPlatformPort> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ShippingConfig> GetShippingConfigAsync(long platformId, CancellationToken ct = default)
    {
        const string path = "internal/platforms/shipping-config";
        var url = $"{path}?platformId={platformId}";

        HttpResponseMessage http;
        try
        {
            http = await _http.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "读取平台运费配置失败（网络异常），平台 {PlatformId}", platformId);
            throw new OrderDownstreamException("商户平台服务", path, ex.Message);
        }

        if (!http.IsSuccessStatusCode)
        {
            _logger.LogError("商户平台服务 {Path} 返回 HTTP {Code}", path, (int)http.StatusCode);
            throw new OrderDownstreamException("商户平台服务", path, $"HTTP {(int)http.StatusCode}");
        }

        ApiResponse<ShippingConfigResponse>? body;
        try
        {
            body = await http.Content.ReadFromJsonAsync<ApiResponse<ShippingConfigResponse>>(ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "商户平台服务 {Path} 的响应无法解析", path);
            throw new OrderDownstreamException("商户平台服务", path, "响应无法解析");
        }

        if (body is null || !body.Success || body.Data is null)
        {
            _logger.LogError("商户平台服务 {Path} 失败（{Code}）：{Message}",
                path, body?.Code, body?.Message);
            throw new OrderDownstreamException("商户平台服务", path, body?.Message ?? "响应为空");
        }

        return new ShippingConfig(body.Data.ShippingFee, body.Data.FreeShippingThreshold);
    }

    /// <summary>运费配置响应数据。</summary>
    /// <param name="PlatformId">平台 Id。</param>
    /// <param name="ShippingFee">平台运费，两位小数。</param>
    /// <param name="FreeShippingThreshold">满额包邮门槛；0 表示不启用。</param>
    private sealed record ShippingConfigResponse(
        [property: JsonPropertyName("platformId")] long PlatformId,
        [property: JsonPropertyName("shippingFee")] decimal ShippingFee,
        [property: JsonPropertyName("freeShippingThreshold")] decimal FreeShippingThreshold);
}

