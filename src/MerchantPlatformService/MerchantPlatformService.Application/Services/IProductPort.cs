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

    /// <summary>校验一批商品能否被装修配置引用。</summary>
    /// <param name="productIds">商品 Id 集合。</param>
    /// <param name="platformId">限定平台，0 表示不限。</param>
    /// <param name="merchantId">限定商户，0 表示不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>不可用的商品及原因；全部可用时列表为空。服务不可用返回 null。</returns>
    /// <remarks>
    /// 装修直接面向顾客展示，能挂未审核 / 未上架商品就等于<b>绕过了审核机制</b>。
    /// 返回 null（服务不可用）时调用方必须<b>拒绝保存</b>，不能当成「全部通过」——
    /// 那是把「查不到」当成「没问题」，审核就形同虚设。
    /// </remarks>
    Task<IReadOnlyList<RejectedProduct>?> CheckForDesignAsync(
        IReadOnlyList<long> productIds, long platformId, long merchantId,
        CancellationToken ct = default);
}

/// <summary>不可用的商品及原因。</summary>
/// <param name="ProductId">商品 Id。</param>
/// <param name="Reason">中文原因，后台要原样展示给运营。</param>
public sealed record RejectedProduct(long ProductId, string Reason);

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

    /// <inheritdoc />
    public async Task<IReadOnlyList<RejectedProduct>?> CheckForDesignAsync(
        IReadOnlyList<long> productIds, long platformId, long merchantId,
        CancellationToken ct = default)
    {
        if (productIds.Count == 0) return [];

        try
        {
            var response = await _http
                .PostAsJsonAsync("internal/products/check-for-design",
                    new { productIds, platformId, merchantId }, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("装修选品校验失败，HTTP {Code}，商品 {Count} 个",
                    (int)response.StatusCode, productIds.Count);
                return null;
            }

            var envelope = await response.Content
                .ReadFromJsonAsync<ApiResponse<DesignCheckResult>>(ct)
                .ConfigureAwait(false);

            if (envelope is null || !envelope.Success || envelope.Data is null) return null;

            return envelope.Data.Rejected ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "调用商品服务装修选品校验异常，商品 {Count} 个", productIds.Count);
            return null;
        }
    }

    /// <summary>商品服务返回的校验结果（只取用得上的字段）。</summary>
    /// <param name="Checkable">可用的商品 Id。</param>
    /// <param name="Rejected">不可用的商品及原因。</param>
    private sealed record DesignCheckResult(
        [property: JsonPropertyName("checkable")] IReadOnlyList<long>? Checkable,
        [property: JsonPropertyName("rejected")] IReadOnlyList<RejectedProduct>? Rejected);
}
