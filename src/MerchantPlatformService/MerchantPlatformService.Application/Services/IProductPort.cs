using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;

namespace MerchantPlatformService.Application.Services;

/// <summary>商品服务端口：商户审核 / 停用时要连带下架其商品。</summary>
public interface IProductPort
{
    /// <summary>批量下架某商户的全部已上架商品。</summary>
    /// <param name="merchantId">商户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下架结果；服务不可用返回 null（调用方据此判定「索引未同步」）。</returns>
 Task<OffShelfOutcome?> OffShelfByMerchantAsync(long merchantId, CancellationToken ct = default);
}

/// <summary>批量下架结果。</summary>
/// <param name="OffShelved">实际下架商品数。</param>
/// <param name="IndexSynced">成功同步索引的数量。</param>
/// <param name="IndexFailed">索引同步失败的数量。</param>
public sealed record OffShelfOutcome(int OffShelved, int IndexSynced, int IndexFailed);

/// <summary>走内网 HTTP 调商品服务的实现。</summary>
public sealed class HttpProductPort : IProductPort
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpProductPort> _logger;

    /// <summary>构造端口。</summary>
    /// <param name="http">指向商品服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpProductPort(HttpClient http, ILogger<HttpProductPort> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OffShelfOutcome?> OffShelfByMerchantAsync(long merchantId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http
                .PostAsJsonAsync("internal/products/off-shelf-by-merchant", new { merchantId }, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("批量下架商户 {MerchantId} 的商品失败，HTTP {Code}",
                    merchantId, (int)response.StatusCode);
                return null;
            }

            var envelope = await response.Content
                .ReadFromJsonAsync<ApiResponse<OffShelfOutcome>>(ct)
                .ConfigureAwait(false);

            if (envelope is null || !envelope.Success || envelope.Data is null) return null;

            return envelope.Data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "调用商品服务批量下架商户 {MerchantId} 的商品异常", merchantId);
            return null;
        }
    }
}
