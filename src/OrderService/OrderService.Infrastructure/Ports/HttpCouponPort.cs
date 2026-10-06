using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;
using OrderService.Domain.Ports;

namespace OrderService.Infrastructure.Ports;

/// <summary>走内网 HTTP 调用营销服务实现券的占用与回退。</summary>
/// <remarks>
/// 与 <c>/internal/**</c> 不同，券的占 / 退挂在公开的 <c>/coupons</c> 上，
/// 因为结算页也要调 <c>/coupons/Settle</c> 做试算——那是 C 端要用的能力，不是纯内部接口。
/// 订单服务只用 <c>Occupy</c> 与 <c>Release</c> 两个写操作。
///
/// <para>序列化用 <c>HttpClient</c> 的默认 Web 选项（camelCase + 大小写不敏感），
/// 与全项目其它内网调用（如 CartService 的商品快照）保持同一套约定，
/// 字段名对得上是靠约定而不是靠显式 JsonPropertyName，所以下游加字段不会打断这里。</para>
/// </remarks>
public sealed class HttpCouponPort : ICouponPort
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpCouponPort> _logger;

    /// <summary>构造端口。</summary>
    /// <param name="http">指向营销服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpCouponPort(HttpClient http, ILogger<HttpCouponPort> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(long CouponId, decimal Discount)> OccupyAsync(
        long customerId, string orderNo, long couponId,
        IReadOnlyList<CouponPortLine> lines, CancellationToken ct = default)
    {
        var command = new OccupyRequest(customerId, orderNo, couponId,
            lines.Select(a => new Line(a.SpuId, a.SkuId, a.Amount)).ToArray());

        var body = await PostAsync<CouponOccupyResponse>("coupons/Occupy", command, ct).ConfigureAwait(false);

        // 走到这里说明业务成功。没占到券时 CouponId 为 0、优惠 0 —— 这不是失败，不要抛。
        return (body.Data?.CouponId ?? 0, body.Data?.DiscountAmount ?? 0m);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(long customerId, string orderNo, CancellationToken ct = default)
    {
        await PostAsync<CouponOccupyResponse>(
            "coupons/Release", new ReleaseRequest(customerId, orderNo), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ConsumeAsync(long customerId, string orderNo, CancellationToken ct = default)
    {
        await PostAsync<CouponOccupyResponse>(
            "coupons/Consume", new ReleaseRequest(customerId, orderNo), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CouponQuoteOption>> QuoteAsync(
        long customerId, IReadOnlyList<CouponPortLine> lines, CancellationToken ct = default)
    {
        var body = await PostAsync<SettleCouponResponse>(
            "coupons/Settle",
            new SettleRequest(customerId, lines.Select(a => new Line(a.SpuId, a.SkuId, a.Amount)).ToArray()),
            ct).ConfigureAwait(false);

        var options = body.Data?.Options ?? [];
        return options
            .Select(a => new CouponQuoteOption(
                a.CouponId, a.CouponTypeName, a.DiscountAmount, a.ExpireAt, a.IsBest))
            .ToList();
    }

    /// <summary>统一发 POST 并检查业务结果。</summary>
    /// <typeparam name="T">下游数据类型。</typeparam>
    /// <param name="path">相对路径。</param>
    /// <param name="payload">请求体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>解析出的统一响应体。</returns>
    /// <exception cref="OrderDownstreamException">HTTP 失败或业务失败时抛出。</exception>
    private async Task<ApiResponse<T>> PostAsync<T>(string path, object payload, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(path, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 网络层失败与业务失败在这里合并成一种异常：编排器对两者一律回滚，无需区分。
            _logger.LogError(ex, "调用营销服务 {Path} 失败（网络异常）", path);
            throw new OrderDownstreamException("营销服务", path, ex.Message);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("营销服务 {Path} 返回 HTTP {Code}", path, (int)response.StatusCode);
            throw new OrderDownstreamException("营销服务", path, $"HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<T>>(ct).ConfigureAwait(false);

        if (body is null)
        {
            _logger.LogError("营销服务 {Path} 返回了无法解析的响应", path);
            throw new OrderDownstreamException("营销服务", path, "响应无法解析");
        }

        if (!body.Success)
        {
            _logger.LogError("营销服务 {Path} 业务失败：{Message}", path, body.Message);
            throw new OrderDownstreamException("营销服务", path, body.Message);
        }

        return body;
    }

    /// <summary>占券请求体。字段名与营销服务的 <c>OccupyCouponCommand</c> 对齐。</summary>
    /// <param name="CustomerId">客户 Id。</param>
    /// <param name="OrderNo">订单号。</param>
    /// <param name="CouponId">指定券 Id，0 表示自动选最优。</param>
    /// <param name="Lines">订单行。</param>
    private sealed record OccupyRequest(long CustomerId, string OrderNo, long CouponId, Line[] Lines);

    /// <summary>订单行。</summary>
    /// <param name="SpuId">SPU Id。</param>
    /// <param name="SkuId">SKU Id。</param>
    /// <param name="Amount">行金额。</param>
    private sealed record Line(long SpuId, long SkuId, decimal Amount);

    /// <summary>占券响应数据。</summary>
    /// <param name="CouponId">实际占用的券 Id，0 表示没占到。</param>
    /// <param name="DiscountAmount">优惠金额。</param>
    /// <param name="AlreadyApplied">是否命中幂等（重复请求）。</param>
    /// <param name="Message">下游提示。</param>
    private sealed record CouponOccupyResponse(
        [property: JsonPropertyName("couponId")] long CouponId,
        [property: JsonPropertyName("discountAmount")] decimal DiscountAmount,
        [property: JsonPropertyName("alreadyApplied")] bool AlreadyApplied,
        [property: JsonPropertyName("message")] string Message);

    /// <summary>回退请求体。</summary>
    /// <param name="CustomerId">客户 Id。</param>
    /// <param name="OrderNo">订单号。</param>
    private sealed record ReleaseRequest(long CustomerId, string OrderNo);

    /// <summary>只读试算请求体。字段名与营销服务的 <c>SettleCouponsCommand</c> 对齐。</summary>
    /// <param name="CustomerId">客户 Id。</param>
    /// <param name="Lines">订单行。</param>
    private sealed record SettleRequest(long CustomerId, Line[] Lines);

    /// <summary>试算响应数据。</summary>
    /// <param name="HasCoupon">是否有可用券。</param>
    /// <param name="Best">最优券；无可用券时为 null。</param>
    /// <param name="Options">全部可用券及各自优惠额。</param>
    private sealed record SettleCouponResponse(
        [property: JsonPropertyName("hasCoupon")] bool HasCoupon,
        [property: JsonPropertyName("best")] SettleCouponOption? Best,
        [property: JsonPropertyName("options")] SettleCouponOption[]? Options);

    /// <summary>一张可用券的试算结果。</summary>
    /// <param name="CouponId">用户券 Id。</param>
    /// <param name="CouponTypeName">券类型中文名。</param>
    /// <param name="DiscountAmount">优惠金额。</param>
    /// <param name="ExpireAt">过期时间。</param>
    /// <param name="IsBest">是否最优。</param>
    private sealed record SettleCouponOption(
        [property: JsonPropertyName("couponId")] long CouponId,
        [property: JsonPropertyName("couponTypeName")] string CouponTypeName,
        [property: JsonPropertyName("discountAmount")] decimal DiscountAmount,
        [property: JsonPropertyName("expireAt")] string ExpireAt,
        [property: JsonPropertyName("isBest")] bool IsBest);
}

/// <summary>下游服务调用失败。</summary>
/// <remarks>
/// 单独一个异常类型，是为了让排障时能明确区分「下游挂了」与「本地代码有 bug」。
/// 编排器目前对两者处理相同（都回滚），但这条信息在日志里值得单独成类。
/// </remarks>
public sealed class OrderDownstreamException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="service">下游服务名。</param>
    /// <param name="path">相对路径。</param>
    /// <param name="reason">失败原因。</param>
    public OrderDownstreamException(string service, string path, string reason)
        : base($"{service} 的 {path} 调用失败：{reason}")
    {
        Service = service;
        Path = path;
        Reason = reason;
    }

    /// <summary>下游服务名。</summary>
    public string Service { get; }

    /// <summary>相对路径。</summary>
    public string Path { get; }

    /// <summary>失败原因。</summary>
    public string Reason { get; }
}
