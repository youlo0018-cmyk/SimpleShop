using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;

namespace EvaluateService.Application.Services;

/// <summary>商品服务端口：把算好的均分回写到商品表。</summary>
public interface IProductPort
{
    /// <summary>回写商品评价均分与条数。</summary>
    /// <param name="ratings">评分条目。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    Task<bool> SyncRatingsAsync(IReadOnlyList<ProductRating> ratings, CancellationToken ct = default);
}

/// <summary>单个商品的评分。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="Score">均分。0 表示无评价。</param>
/// <param name="Count">首评条数。</param>
public sealed record ProductRating(long SpuId, decimal Score, int Count);

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
    public async Task<bool> SyncRatingsAsync(IReadOnlyList<ProductRating> ratings,
        CancellationToken ct = default)
    {
        if (ratings.Count == 0) return true;

        try
        {
            var body = new
            {
                items = ratings.Select(a => new { spuId = a.SpuId, score = a.Score, count = a.Count }).ToList()
            };

            var response = await _http
                .PostAsJsonAsync("internal/products/ratings/sync", body, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("回写商品评分失败，HTTP {Code}，条目 {Count}",
                    (int)response.StatusCode, ratings.Count);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "调用商品服务回写评分异常，条目 {Count}", ratings.Count);
            return false;
        }
    }
}
