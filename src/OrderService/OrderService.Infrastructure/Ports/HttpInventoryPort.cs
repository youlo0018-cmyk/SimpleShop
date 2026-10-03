using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;
using OrderService.Domain.Ports;

namespace OrderService.Infrastructure.Ports;

/// <summary>走内网 HTTP 调用库存服务实现锁定与释放。</summary>
/// <remarks>
/// 复用库存服务的 <c>/internal/inventory/Apply</c>，用动作名区分锁定与释放：
/// lock = locked+q / available−q；release = locked−q / available+q（BUSINESS.md 9.2）。
/// 不为订单服务再单开一套接口——库存记账只有一个入口，才不会出现两条路径算出不同数字。
/// </remarks>
public sealed class HttpInventoryPort : IInventoryPort
{
    private const string LockAction = "lock";
    private const string ReleaseAction = "release";
    private const string DeductAction = "deduct";
    private const string ReplenishAction = "replenish";

    private readonly HttpClient _http;
    private readonly ILogger<HttpInventoryPort> _logger;

    /// <summary>构造端口。</summary>
    /// <param name="http">指向库存服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpInventoryPort(HttpClient http, ILogger<HttpInventoryPort> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> LockAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default)
    {
        return await SendAsync(skuId, LockAction, quantity, bizNo, allowShortage: true, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default)
    {
        await SendAsync(skuId, ReleaseAction, quantity, bizNo, allowShortage: false, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeductAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default)
    {
        await SendAsync(skuId, DeductAction, quantity, bizNo, allowShortage: false, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReplenishAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default)
    {
        await SendAsync(skuId, ReplenishAction, quantity, bizNo, allowShortage: false, ct).ConfigureAwait(false);
    }

    /// <summary>发起一次库存变更并判定结果。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="action">动作，lock / release / deduct。</param>
    /// <param name="quantity">数量。</param>
    /// <param name="bizNo">业务单号。</param>
    /// <param name="allowShortage">
    /// 库存不足时是否只返回 false 而不抛异常。<b>只有锁定允许</b>：
    /// 锁定不足是正常的「货不够」，要告诉下单链路；
    /// 释放与扣减不足说明账已经乱了，必须抛出来让人去查，静默吞掉等于库存对不上却没人知道。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true；允许时库存不足返回 false。</returns>
    /// <exception cref="OrderDownstreamException">网络失败、HTTP 失败、库存记录不存在、请求不合法，或不允许的库存不足时抛出。</exception>
    private async Task<bool> SendAsync(
        long skuId, string action, int quantity, string bizNo, bool allowShortage, CancellationToken ct)
    {
        var path = "internal/inventory/Apply";
        var payload = new ApplyRequest(skuId, action, quantity, bizNo, "订单占用", 0, 0);

        HttpResponseMessage http;
        try
        {
            http = await _http.PostAsJsonAsync(path, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "调用库存服务 {Path} 失败（网络异常）", path);
            throw new OrderDownstreamException("库存服务", path, ex.Message);
        }

        if (!http.IsSuccessStatusCode)
        {
            _logger.LogError("库存服务 {Path} 返回 HTTP {Code}", path, (int)http.StatusCode);
            throw new OrderDownstreamException("库存服务", path, $"HTTP {(int)http.StatusCode}");
        }

        var body = await http.Content.ReadFromJsonAsync<ApiResponse<StockChangeResponse>>(ct).ConfigureAwait(false);
        if (body is null)
        {
            _logger.LogError("库存服务 {Path} 返回了无法解析的响应", path);
            throw new OrderDownstreamException("库存服务", path, "响应无法解析");
        }

        if (body.Success) return true;

        // 只有 4001 StockNotEnough 才算「货不够」。库存记录不存在（商品没初始化库存）
        // 是数据问题，抛出去才能让下单报出真正的错，而不是一句「库存不足」把问题盖住。
        if (allowShortage && body.Code == (int)BaseApiResponseCode.StockNotEnough)
        {
            _logger.LogInformation("SKU {SkuId} 库存不足：{Message}", skuId, body.Message);
            return false;
        }

        _logger.LogError("库存服务 {Path} 失败（{Code}）：{Message}", path, body.Code, body.Message);
        throw new OrderDownstreamException("库存服务", path, body.Message);
    }

    /// <summary>库存变更请求体。字段与库存服务的 <c>ApplyStockCommand</c> 对齐。</summary>
    /// <param name="SkuId">SKU Id。</param>
    /// <param name="Action">动作。</param>
    /// <param name="Quantity">数量。</param>
    /// <param name="BizNo">业务单号。</param>
    /// <param name="Remark">备注，写进流水。</param>
    /// <param name="PlatformId">平台 Id。</param>
    /// <param name="MerchantId">商户 Id。</param>
    private sealed record ApplyRequest(
        long SkuId, string Action, int Quantity, string BizNo, string Remark, long PlatformId, long MerchantId);

    /// <summary>库存变更响应数据。</summary>
    /// <param name="SkuId">SKU Id。</param>
    /// <param name="Available">变更后可用。</param>
    /// <param name="Locked">变更后锁定。</param>
    /// <param name="Deducted">变更后已扣减。</param>
    /// <param name="AlreadyApplied">是否命中幂等。</param>
    private sealed record StockChangeResponse(
        [property: JsonPropertyName("skuId")] long SkuId,
        [property: JsonPropertyName("available")] int Available,
        [property: JsonPropertyName("locked")] int Locked,
        [property: JsonPropertyName("deducted")] int Deducted,
        [property: JsonPropertyName("alreadyApplied")] bool AlreadyApplied);
}