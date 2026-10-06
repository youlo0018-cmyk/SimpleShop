using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using CustomerService.Application.Services;
using Microsoft.Extensions.Logging;

namespace CustomerService.Infrastructure.Services;

/// <summary>走内网 HTTP 调积分服务发放注册赠送积分（BUSINESS.md 13.2）。</summary>
/// <remarks>
/// <para><b>金额不由这里决定</b>：注册赠送的数额是积分规则里的一项
/// （<c>register_gift</c>，后台可改），所以调的是积分服务的
/// <c>EarnRegisterGift</c>，由积分服务自己读规则、自己拼幂等键
/// <c>REG-{customerId}</c>。</para>
///
/// <para>之前这里传的是写死的 100 —— 运营把赠送改成 200，客户服务这边还是 100，
/// 而且不会有任何报错，只是「改了不生效」。</para>
///
/// <para><b>发放失败不让注册失败</b>：客户已经建好了，为了 100 积分把注册整体回滚，
/// 用户会看到一个「注册失败」却不知道为什么。返回 false 让注册照常成功，
/// 由补偿任务补发 —— 这与订单侧「积分发失败不回滚订单」是同一套取舍。</para>
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
    public async Task<bool> TryGrantRegistrationBonusAsync(long customerId, CancellationToken ct = default)
    {
        // 只传客户 Id，金额由积分服务按规则决定（规则在它那边，它才是归属方）。
        const string path = "internal/points/EarnRegisterGift";
        var payload = new { customerId };

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

            _logger.LogInformation("客户 {CustomerId} 注册赠送积分已发放", customerId);
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

