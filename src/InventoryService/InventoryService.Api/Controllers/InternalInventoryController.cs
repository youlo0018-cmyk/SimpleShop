using Collaboration.Domain.Common;
using InventoryService.Application.Features.Internal;
using InventoryService.Application.Features.Operations;
using InventoryService.Domain.IRepository;
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
    private readonly IStockRepository _stocks;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    /// <param name="stocks">库存仓储。报表的预警数直接读仓储，不走命令。</param>
    public InternalInventoryController(IMediator mediator, IStockRepository stocks)
    {
        _mediator = mediator;
        _stocks = stocks;
    }

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
    }

    /// <summary>重试补偿表里到期的库存释放（ScheduledService 每轮调用）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>本轮处理统计。</returns>
    /// <remarks>
    /// 释放库存是取消订单 / 超时关单的**兜底动作**，它自己失败时库存就永久锁住——
    /// 商品一直卖不出去，而且没有任何痕迹说明发生过什么。释放失败会写
    /// <c>pending_stock_release</c>，这个接口负责把它们重试回来。
    /// </remarks>
    [HttpPost("compensate-releases")]
    public Task<ApiResponse<CompensateStockReleasesResult>> CompensateReleases(
        [FromBody] CompensateStockReleasesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>列出疑似孤儿锁定：已超期、且从未被释放 / 扣减。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>候选清单。</returns>
    /// <remarks>
    /// 下单链路是「锁库存 → 建订单」。进程恰好死在两步之间时，库存会**永远锁着**
    /// 而订单根本不存在，没有任何补偿路径能找得回来。这个接口就是那条路径的入口。
    /// </remarks>
    [HttpPost("reconcile/orphan-locks")]
    public Task<ApiResponse<List<OrphanLockCandidate>>> OrphanLocks(
        [FromBody] QueryOrphanLocksCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>释放已确认无对应订单的孤儿锁定。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>释放统计。</returns>
    /// <remarks>
    /// <b>库存服务不判断「有没有订单」</b>——那要问订单服务。
    /// 调用方要先拿「确实没有订单」的结论再来释放，否则两个服务会耦在一起。
    /// </remarks>
    [HttpPost("reconcile/release-orphans")]
    public Task<ApiResponse<ReleaseOrphanLocksResult>> ReleaseOrphans(
        [FromBody] ReleaseOrphanLocksCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>统计低于预警阈值的 SKU 数（工作台报表用）。</summary>
    /// <param name="query">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>预警 SKU 数。</returns>
    /// <remarks>
    /// 预警数是库存的口径，必须由库存服务自己算：订单服务跨库查不到预警阈值，
    /// 硬让它自己数就得把阈值复制一份过去，运营改了阈值两边就对不上了。
    /// </remarks>
    [HttpPost("LowStock/Count")]
    public async Task<ApiResponse<LowStockCountResult>> CountLowStock(
        [FromBody] LowStockCountQuery query, CancellationToken ct)
    {
        var count = await _stocks.CountLowStockAsync(
            query.MerchantId, query.PlatformId, ct).ConfigureAwait(false);

        return ApiResults.Ok(new LowStockCountResult(checked((int)count)));
    }
}

/// <summary>预警数查询条件。</summary>
/// <param name="MerchantId">商户 Id，0 表示不限。</param>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
public sealed record LowStockCountQuery(long MerchantId = 0, long PlatformId = 0);

/// <summary>预警数结果。</summary>
/// <param name="Count">低于预警阈值的 SKU 数。</param>
public sealed record LowStockCountResult(int Count);
