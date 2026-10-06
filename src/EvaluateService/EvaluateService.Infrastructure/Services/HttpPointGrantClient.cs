using System.Net.Http.Json;
using Collaboration.Domain.Common;
using EvaluateService.Application.Services;
using Microsoft.Extensions.Logging;

namespace EvaluateService.Infrastructure.Services;

/// <summary>走内网 HTTP 调积分服务发放「发表首评」的赠送积分（BUSINESS.md 13.2）。</summary>
/// <remarks>
/// <para><b>金额不由这里决定</b>：首评赠送的数额是积分规则里的一项
/// （<c>first_evaluate_gift</c>，后台可改），所以调的是积分服务的
/// <c>EarnEvaluateGift</c>，由积分服务自己读规则、自己拼幂等键
/// <c>EVL-{evaluateId}</c>。</para>
///
/// <para>🔴 这条途径此前**完全没有实现**：BUSINESS.md 13.2 写着「发表首评 +20」，
/// 20.1 写着由 PointService 消费 <c>evaluate.created</c>，
/// 但 <c>evaluate.created</c> 只在 EventTopics 里声明过，没有任何地方发布，
/// 评价服务里也没有任何发积分的代码。实测发表首评后 totalEarned 一点没变。</para>
/// </remarks>
public sealed class HttpPointGrantClient : IPointGrantClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpPointGrantClient> _logger;

    /// <summary>构造客户端。</summary>
    /// <param name="http">指向积分服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpPointGrantClient(HttpClient http, ILogger<HttpPointGrantClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> TryGrantEvaluateBonusAsync(
        long customerId, long evaluateId, CancellationToken ct = default)
    {
        // 只传客户与评价 Id，金额由积分服务按规则决定（规则在它那边，它才是归属方）。
        const string path = "internal/points/EarnEvaluateGift";
        var payload = new { customerId, evaluateId };

        try
        {
            var response = await _http.PostAsJsonAsync(path, payload, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("发表首评赠送积分失败：HTTP {Code}（评价 {EvaluateId}）",
                    (int)response.StatusCode, evaluateId);
                return false;
            }

            var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>(ct).ConfigureAwait(false);
            if (body is null || !body.Success)
            {
                _logger.LogError("发表首评赠送积分失败（{Code}）：{Message}（评价 {EvaluateId}）",
                    body?.Code, body?.Message, evaluateId);
                return false;
            }

            _logger.LogInformation("评价 {EvaluateId} 的首评赠送积分已发放给客户 {CustomerId}",
                evaluateId, customerId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 积分服务抖动不该让评价发不出去 —— 返回 false，由补偿任务补发。
            _logger.LogError(ex, "调用积分服务发放发表首评赠送失败（评价 {EvaluateId}）", evaluateId);
            return false;
        }
    }
}

