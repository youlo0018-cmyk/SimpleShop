using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;
using OrderService.Domain.Ports;
using OrderService.Domain.Services;

namespace OrderService.Infrastructure.Ports;

/// <summary>走内网 HTTP 调用积分服务实现冻结 / 解冻 / 实扣。</summary>
/// <remarks>
/// 积分接口挂在 <c>/internal/points</c> 下，网关不路由该前缀（见 ocelot.json 的安全说明），
/// 所以只可能由服务间调用，没有 C 端直接访问的路径。
/// </remarks>
public sealed class HttpPointPort : IPointPort
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpPointPort> _logger;

    /// <summary>构造端口。</summary>
    /// <param name="http">指向积分服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpPointPort(HttpClient http, ILogger<HttpPointPort> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> LockAsync(long customerId, string orderNo, long points, CancellationToken ct = default)
    {
        var response = await SendAsync(
            "internal/points/Lock",
            new LockRequest(customerId, orderNo, points, "订单冻结"),
            ct).ConfigureAwait(false);

        if (response.Success) return true;

        // 积分不足是正常业务（用户余额不够），不算故障。
        // 只认 QuotaNotEnough（4004）；其它码说明积分服务本身有问题，要抛出去让编排器回滚。
        if (response.Code == (int)BaseApiResponseCode.QuotaNotEnough)
        {
            _logger.LogInformation("客户 {CustomerId} 积分不足：{Message}", customerId, response.Message);
            return false;
        }

        _logger.LogError("积分服务 Lock 失败（{Code}）：{Message}", response.Code, response.Message);
        throw new OrderDownstreamException("积分服务", "internal/points/Lock", response.Message);
    }

    /// <inheritdoc />
    public async Task UnfreezeAsync(long customerId, string orderNo, CancellationToken ct = default)
    {
        await SendAsync(
            "internal/points/Unfreeze",
            new BizRequest(customerId, orderNo, "订单回滚解冻"),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ConsumeAsync(long customerId, string orderNo, CancellationToken ct = default)
    {
        await SendAsync(
            "internal/points/Consume",
            new BizRequest(customerId, orderNo, "支付成功实扣"),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task EarnByOrderAsync(long customerId, string orderNo, decimal paidAmount, CancellationToken ct = default)
    {
        await SendAsync(
            "internal/points/EarnByOrder",
            new EarnByOrderRequest(customerId, orderNo, paidAmount, "订单完成发放"),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> GetDeductionRateAsync(CancellationToken ct = default)
    {
        const string path = "internal/points/DeductionRate";

        try
        {
            var body = await _http
                .GetFromJsonAsync<ApiResponse<DeductionRateResponse>>(path, ct)
                .ConfigureAwait(false);

            if (body is null || !body.Success || body.Data is null || body.Data.PointsPerYuan <= 0)
            {
                // 拿不到就用兜底汇率并告警。**不能抛**：汇率只影响积分抵扣的换算，
                // 为它让整单下不了，代价比「这一单按默认汇率算」大得多。
                _logger.LogWarning("读取积分抵扣汇率失败（{Message}），本次按默认 100 计算",
                    body?.Message ?? "响应为空");
                return OrderAmountCalculator.DefaultPointsPerYuan;
            }

            return body.Data.PointsPerYuan;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "调用积分服务读取抵扣汇率异常，本次按默认 100 计算");
            return OrderAmountCalculator.DefaultPointsPerYuan;
        }
    }

    /// <summary>抵扣汇率响应数据。</summary>
    /// <param name="PointsPerYuan">多少积分抵 1.00 元。</param>
    private sealed record DeductionRateResponse(
        [property: JsonPropertyName("pointsPerYuan")] long PointsPerYuan);

    /// <inheritdoc />
    public async Task RecoverByRefundAsync(
        long customerId, string orderNo, decimal refundRatio, CancellationToken ct = default)
    {
        // 比例不在这里算：只有退款方知道退了多少，积分服务不认订单金额，
        // 让它自己算就得再查一遍订单 —— 金额口径会分叉。
        var body = await SendAsync(
            "internal/points/Refund",
            new RefundRequest(customerId, orderNo, refundRatio, "退款按比例回收积分"),
            ct).ConfigureAwait(false);

        // 业务失败要往上抛：静默吞掉的话，客户一边拿回钱一边留着白拿的积分，
        // 而日志里只有一行 warn，事后对账才看得出来。
        if (!body.Success)
        {
            _logger.LogError(
                "积分服务回收失败：订单 {OrderNo} 比例 {Ratio}，原因 {Message}",
                orderNo, refundRatio, body.Message);
            throw new OrderDownstreamException("积分服务", "internal/points/Refund", body.Message);
        }
    }

    /// <summary>统一发 POST 并检查业务结果。</summary>
    /// <param name="path">相对路径。</param>
    /// <param name="payload">请求体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>统一响应体，失败时由调用方按码判断语义。</returns>
    /// <exception cref="OrderDownstreamException">网络失败、HTTP 失败或响应无法解析时抛出。</exception>
    private async Task<ApiResponse<PointBalanceResponse>> SendAsync(string path, object payload, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(path, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "调用积分服务 {Path} 失败（网络异常）", path);
            throw new OrderDownstreamException("积分服务", path, ex.Message);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("积分服务 {Path} 返回 HTTP {Code}", path, (int)response.StatusCode);
            throw new OrderDownstreamException("积分服务", path, $"HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PointBalanceResponse>>(ct).ConfigureAwait(false);
        if (body is null)
        {
            _logger.LogError("积分服务 {Path} 返回了无法解析的响应", path);
            throw new OrderDownstreamException("积分服务", path, "响应无法解析");
        }

        return body;
    }

    /// <summary>冻结请求体。</summary>
    /// <param name="CustomerId">客户 Id。</param>
    /// <param name="BizNo">订单号。</param>
    /// <param name="Quantity">冻结数量。</param>
    /// <param name="Remark">备注。</param>
    private sealed record LockRequest(long CustomerId, string BizNo, long Quantity, string Remark);

    /// <summary>按订单发放的请求体。</summary>
    /// <param name="CustomerId">客户 Id。</param>
    /// <param name="BizNo">订单号。</param>
    /// <param name="PaidAmount">实付金额，积分数由积分服务按规则算。</param>
    /// <param name="Remark">备注。</param>
    private sealed record EarnByOrderRequest(long CustomerId, string BizNo, decimal PaidAmount, string Remark);

    /// <summary>解冻 / 实扣请求体。</summary>
    /// <param name="CustomerId">客户 Id。</param>
    /// <param name="BizNo">订单号。</param>
    /// <param name="Remark">备注。</param>
    private sealed record BizRequest(long CustomerId, string BizNo, string Remark);

    /// <summary>退款回收积分请求体。</summary>
    /// <param name="CustomerId">客户 Id。</param>
    /// <param name="BizNo">订单号。</param>
    /// <param name="RefundRatio">退款比例 0~1，整单退传 1。</param>
    /// <param name="Remark">备注。</param>
    private sealed record RefundRequest(long CustomerId, string BizNo, decimal RefundRatio, string Remark);

    /// <summary>积分余额响应数据。</summary>
    /// <param name="CustomerId">客户 Id。</param>
    /// <param name="Available">可用积分。</param>
    /// <param name="Frozen">冻结积分。</param>
    /// <param name="TotalEarned">累计发放。</param>
    /// <param name="TotalUsed">累计消耗。</param>
    /// <param name="AlreadyApplied">是否命中幂等。</param>
    private sealed record PointBalanceResponse(
        [property: JsonPropertyName("customerId")] long CustomerId,
        [property: JsonPropertyName("available")] long Available,
        [property: JsonPropertyName("frozen")] long Frozen,
        [property: JsonPropertyName("totalEarned")] long TotalEarned,
        [property: JsonPropertyName("totalUsed")] long TotalUsed,
        [property: JsonPropertyName("alreadyApplied")] bool AlreadyApplied);
}
