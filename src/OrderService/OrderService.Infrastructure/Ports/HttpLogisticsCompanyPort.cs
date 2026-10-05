using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;
using OrderService.Domain.Ports;

namespace OrderService.Infrastructure.Ports;

/// <summary>走内网 HTTP 调用商品服务取物流公司名称。</summary>
/// <remarks>
/// 走 <c>/internal/products/logistics-companies/{id}</c>，网关不路由 <c>/internal</c> 前缀，
/// 所以只可能由服务间调用，没有被外部直接访问的路径。
/// </remarks>
public sealed class HttpLogisticsCompanyPort : ILogisticsCompanyPort
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpLogisticsCompanyPort> _logger;

    /// <summary>构造端口。</summary>
    /// <param name="http">指向商品服务的 HttpClient。</param>
    /// <param name="logger">日志器。</param>
    public HttpLogisticsCompanyPort(HttpClient http, ILogger<HttpLogisticsCompanyPort> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> ResolveNameAsync(long logisticsCompanyId, CancellationToken ct = default)
    {
        var path = $"internal/products/logistics-companies/{logisticsCompanyId}";

        HttpResponseMessage http;
        try
        {
            http = await _http.GetAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 网络层故障与「公司不存在」是两回事：前者要抛出去让人重试发货，
            // 后者由 Handler 回「物流公司不存在」。混成 null 会把基础设施问题
            // 报成一句误导性的「请选择物流公司」。
            _logger.LogError(ex, "调用商品服务 {Path} 失败（网络异常）", path);
            throw new OrderDownstreamException("商品服务", path, ex.Message);
        }

        // 404 是**正常结果**：前端可能拿到一张过期的公司列表（公司刚被删）。
        // 这里回 null 让 Handler 给出可读的提示，不当成故障抛异常。
        if (http.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("物流公司 {Id} 在商品服务中不存在", logisticsCompanyId);
            return null;
        }

        if (!http.IsSuccessStatusCode)
        {
            _logger.LogError("商品服务 {Path} 返回 HTTP {Code}", path, (int)http.StatusCode);
            throw new OrderDownstreamException("商品服务", path, $"HTTP {(int)http.StatusCode}");
        }

        var body = await http.Content
            .ReadFromJsonAsync<ApiResponse<CompanyResponse>>(ct).ConfigureAwait(false);

        if (body is null || !body.Success || body.Data is null)
        {
            _logger.LogError("商品服务 {Path} 返回了无法解析的响应", path);
            throw new OrderDownstreamException("商品服务", path, "响应无法解析");
        }

        return body.Data.CompanyName;
    }

    /// <summary>物流公司响应数据。</summary>
    /// <param name="LogisticsId">物流公司 Id。</param>
    /// <param name="CompanyName">公司名称。</param>
    /// <param name="Status">1 启用 / 2 停用。</param>
    private sealed record CompanyResponse(
        [property: JsonPropertyName("logisticsId")] long LogisticsId,
        [property: JsonPropertyName("companyName")] string CompanyName,
        [property: JsonPropertyName("status")] int Status);
}
