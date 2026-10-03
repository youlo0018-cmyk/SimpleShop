using Collaboration.Domain.Common;
using InventoryService.Application.Features.Operations;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace InventoryService.Api.Controllers;

/// <summary>库存内部接口，供订单 / 支付 / 秒杀链路调用。网关不路由 /internal 前缀。</summary>
/// <remarks>
/// 文档里写的是 gRPC（REVIEW.md 链路 8），本项目实际统一走内网 HTTP——
/// 与 UserService / PermissionService 的 /internal 保持同一套约定，
/// 不为单个服务引入第二套通信方式。
/// </remarks>
[ApiController]
[Route("internal/inventory")]
public sealed class InternalInventoryController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public InternalInventoryController(IMediator mediator) => _mediator = mediator;

    /// <summary>初始化库存（商品创建时调用）。</summary>
    /// <param name="command">初始化命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回初始库存；重复初始化按幂等返回现有值。</returns>
    [HttpPost("Init")]
    public Task<ApiResponse<StockChangeResult>> Init([FromBody] InitStockCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>库存变更：锁定 / 扣减 / 释放 / 回补。</summary>
    /// <param name="command">变更命令，BizNo 是幂等键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回变更后的三个计数；库存不足返回业务错误。</returns>
    [HttpPost("Apply")]
    public Task<ApiResponse<StockChangeResult>> Apply([FromBody] ApplyStockCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>按 SKU Id 集合批量取库存快照。</summary>
    /// <param name="skuIds">SKU Id 集合，逗号分隔。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的库存记录。没初始化过库存的 SKU 不会出现在结果里。</returns>
    /// <remarks>
    /// 给下单前的库存预检用：一次拿多个 SKU，避免订单服务逐个调。
    /// 「没出现」代表该 SKU 没有库存记录，调用方要当成 0 处理而不是当作查失败。
    /// </remarks>
    [HttpGet("Snapshot")]
    public async Task<ApiResponse<List<StockItem>>> Snapshot(
        [FromQuery] string skuIds, CancellationToken ct = default)
    {
        var ids = (skuIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => long.TryParse(a, out var v) ? v : 0)
            .Where(a => a > 0)
            .Distinct()
            .ToArray();

        if (ids.Length == 0)
        {
            return ApiResults.Fail<List<StockItem>>(BaseApiResponseCode.BadRequest, "请提供至少一个 SKU Id");
        }

        return await _mediator.Send(new QueryStocksByIdsCommand(ids), ct);
    }}