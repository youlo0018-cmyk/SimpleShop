using System.Net.Http.Json;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;
using OrderService.Domain.Ports;

namespace OrderService.Infrastructure.Ports;

/// <summary>走内网 HTTP 调营销服务，把秒杀单退掉的货还回秒杀池。</summary>
/// <remarks>
/// 独立一个端口而不是塞进 <see cref="ICouponPort"/>：两者虽然都指向营销服务，
/// 但一个管券、一个管秒杀，混在一起会让「改券逻辑」的人顺手改到库存。
/// </remarks>
public sealed class HttpSeckillPort : ISeckillPort
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpSeckillPort> _logger;

    /// <summary>构造端口。</summary>
    /// <param name="http">指向营销服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpSeckillPort(HttpClient http, ILogger<HttpSeckillPort> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ReleaseGrabAsync(
        long customerId, long skuId, int quantity, string orderNo, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(
                "internal/marketing/seckill/grabs/Release",
                new ReleaseRequest(customerId, skuId, quantity, orderNo),
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "秒杀货退回接口 HTTP {Code}：订单 {OrderNo}", (int)response.StatusCode, orderNo);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 不抛：钱已经退给客户了，为 1 件货把整笔退款搞失败反而更糟。
            // 记 error 事后对账，人工把 sold_count 补回去即可。
            _logger.LogError(ex,
                "秒杀货退回调用失败：订单 {OrderNo} SKU {SkuId} {Quantity} 件，需人工核对 sold_count",
                orderNo, skuId, quantity);
        }
    }

    /// <summary>请求体。</summary>
    /// <param name="CustomerId">客户 Id。</param>
    /// <param name="SkuId">SKU Id。</param>
    /// <param name="Quantity">退回件数。</param>
    /// <param name="OrderNo">订单号。</param>
    private sealed record ReleaseRequest(long CustomerId, long SkuId, int Quantity, string OrderNo);
}
