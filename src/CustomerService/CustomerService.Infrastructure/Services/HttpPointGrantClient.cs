using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using CustomerService.Application.Services;
using Microsoft.Extensions.Logging;

namespace CustomerService.Infrastructure.Services;

/// <summary>走内网 HTTP 调积分服务发放注册赠送积分（BUSINESS.md 13.2）。</summary>
/// <remarks>
/// <para>幂等靠 <c>bizNo</c>：同一个客户重复注册 / 重试只会发一次。
/// 积分服务侧按 <c>{bizNo}:{action}</c> 判重，所以这里用固定的
/// <c>REG-{customerId}</c> 而不是随机值。</para>
///
/// <para><b>发放失败不让注册失败</b>：客户已经建好了，为了 100 积分把注册整体回滚，
/// 用户会看到一个「注册失败」却不知道为什么。返回 false 让注册照常成功，
/// 由补偿任务补发 —— 这与订单侧「积分发失败不回滚订单」是同一套取舍。</para>
/// </remarks>
public sealed class HttpPointGrantClient : IPointGrantClient
{
    /// <summary>注册赠送的积分数（BUSINESS.md 13.2）。</summary>
    private const long RegistrationBonus = 100;

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
    public async Task<bool> TryGrantRegistrationBonusAsync(long customerId, CancellationToken ct = default)
    {
        const string path = "internal/points/Earn";
        var payload = new
        {
            customerId,
            source = "注册赠送",
            quantity = RegistrationBonus,
            bizNo = $"REG-{customerId}",
            remark = "新客户注册赠送",
            action = "earn",
        };

        try
        {
            var response = await _http.PostAsJsonAsync(path, payload, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("注册赠送积分失败：HTTP {Code}（客户 {CustomerId}）",
                    (int)response.StatusCode, customerId);
                return false;
            }

            var body = await response.Content
                .ReadFromJsonAsync<ApiResponse<PointBalanceResponse>>(ct).ConfigureAwait(false);

            if (body is null || !body.Success)
            {
                _logger.LogError("注册赠送积分失败（{Code}）：{Message}（客户 {CustomerId}）",
                    body?.Code, body?.Message, customerId);
                return false;
            }

            _logger.LogInformation("客户 {CustomerId} 注册赠送 {Points} 积分已发放",
                customerId, RegistrationBonus);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 积分服务抖动不该让客户注册不了 —— 返回 false，由补偿任务补发。
            _logger.LogError(ex, "调用积分服务发放注册赠送失败（客户 {CustomerId}）", customerId);
            return false;
        }
    }

    /// <summary>积分服务返回的余额快照。</summary>
    /// <param name="Available">可用积分。</param>
    /// <param name="Frozen">冻结积分。</param>
    /// <param name="TotalEarned">累计获得。</param>
    /// <param name="TotalUsed">累计消耗。</param>
    private sealed record PointBalanceResponse(
        [property: JsonPropertyName("available")] long Available,
        [property: JsonPropertyName("frozen")] long Frozen,
        [property: JsonPropertyName("totalEarned")] long TotalEarned,
        [property: JsonPropertyName("totalUsed")] long TotalUsed);
}

