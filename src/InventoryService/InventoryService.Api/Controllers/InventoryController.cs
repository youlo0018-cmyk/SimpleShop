using Collaboration.Domain.Common;
using InventoryService.Application.Features.Operations;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace InventoryService.Api.Controllers;

/// <summary>库存管理（后台）。锁定与已扣减**不可手工修改**，只能走订单 / 支付 / 取消 / 退款流程。</summary>
[ApiController]
[Route("inventory")]
public sealed class InventoryController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public InventoryController(IMediator mediator) => _mediator = mediator;

    /// <summary>分页查询库存。</summary>
    /// <param name="page">页码。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="keyword">按商品名 / 规格文本模糊搜索。</param>
    /// <param name="lowStockOnly">只看低于预警阈值的。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>库存列表。</returns>
    [HttpGet("List")]
    public Task<ApiResponse<List<StockItem>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string keyword = "",
        [FromQuery] bool lowStockOnly = false,
        CancellationToken ct = default)
        => _mediator.Send(new QueryStocksCommand(page, pageSize, keyword, lowStockOnly), ct);

    /// <summary>手工调整可用库存。</summary>
    /// <param name="command">调整命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回调整后的库存。</returns>
    /// <remarks>提交的是<b>调整量</b>（+10 / -5）而不是最终值，理由见处理器注释。</remarks>
    [HttpPost("Adjust")]
    public Task<ApiResponse<StockChangeResult>> Adjust([FromBody] AdjustStockCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>查询某 SKU 的库存流水。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="limit">最多返回多少条。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>流水列表，倒序。</returns>
    [HttpGet("Flows")]
    public Task<ApiResponse<List<StockFlowItem>>> Flows(
        [FromQuery] long skuId, [FromQuery] int limit = 50, CancellationToken ct = default)
        => _mediator.Send(new QueryStockFlowsCommand(skuId, limit), ct);
}