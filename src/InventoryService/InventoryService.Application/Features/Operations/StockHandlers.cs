using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using Collaboration.Domain.Infrastructure;
using InventoryService.Domain.Entities;
using InventoryService.Domain.IRepository;
using MediatR;
using Microsoft.Extensions.Logging;

namespace InventoryService.Application.Features.Operations;

/// <summary>库存变更处理器（锁定 / 扣减 / 释放 / 回补）。</summary>
/// <remarks>
/// 这一层只负责「决定要做什么」与「把结果翻译成响应」；
/// 幂等、防超卖、非负校验全在仓储的 ApplyAsync 里，因为它们必须与写库在同一个事务内，
/// 拆开就等于没有。
/// </remarks>
public sealed class ApplyStockHandler : IRequestHandler<ApplyStockCommand, ApiResponse<StockChangeResult>>
{
    private readonly IStockRepository _stocks;
    private readonly ILogger<ApplyStockHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="stocks">库存仓储。</param>
    /// <param name="logger">日志器。</param>
    public ApplyStockHandler(IStockRepository stocks, ILogger<ApplyStockHandler> logger)
    {
        _stocks = stocks;
        _logger = logger;
    }

    /// <summary>执行变更。</summary>
    /// <param name="request">变更命令，格式已由校验器保证。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回变更后的三个计数；库存不足返回 400。</returns>
    public async Task<ApiResponse<StockChangeResult>> Handle(ApplyStockCommand request, CancellationToken ct)
    {
        var ctx = TenantContextHolder.Current;
        var operation = new StockOperation(
            request.SkuId, request.Action, request.Quantity, request.BizNo,
            request.Remark, request.PlatformId, request.MerchantId);

        StockApplyOutcome outcome;
        try
        {
            outcome = await _stocks.ApplyAsync(operation, ctx.UserId, ctx.UserName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 释放是「取消订单 / 超时关单 / 下单失败回滚」的兜底动作，它自己挂掉时
            // 库存会**永久锁住**——商品一直卖不出去，而且没有任何痕迹说明发生过什么。
            // 记一条待补偿交给定时任务重试，是唯一能让它自己回来的办法。
            if (operation.Action == StockActions.Release || operation.Action == StockActions.SeckillRelease)
            {
                await RecordPendingReleaseAsync(operation, ex.Message, ct);
            }

            throw;
        }

        if (!outcome.Succeeded)
        {
            // 库存不足单独用 4001，调用方（尤其下单链路）要能把「货不够」与
            // 「库存记录不存在 / 请求不合法」区分开：前者是正常业务提示，
            // 后者是数据或程序问题，混成一个码会让下单把两种故障都报成「库存不足」。
            var code = outcome.Failure == StockApplyFailure.Shortage
                ? BaseApiResponseCode.StockNotEnough
                : BaseApiResponseCode.BusinessError;

            return ApiResults.Fail<StockChangeResult>(code, outcome.Error);
        }

        var result = new StockChangeResult(
            request.SkuId, outcome.Available, outcome.Locked, outcome.Deducted, outcome.AlreadyApplied);

        // 重复请求是正常业务（订单重试、消息重投），回 200 并说明已处理过，
        // 不能报错让上游以为这次失败了。
        return ApiResults.Ok(result, outcome.AlreadyApplied ? "该业务单已处理过，返回首次结果" : "操作成功");
    }

    /// <summary>记一条待补偿的释放，让定时任务之后重试。</summary>
    /// <param name="operation">失败的释放操作。</param>
    /// <param name="error">失败原因。</param>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>
    /// <b>这里自己也不能抛</b>：补偿记录写不进去说明数据库已经出问题了，
    /// 再抛一次只会把原始异常盖掉，而原始异常（释放失败）才是排查的起点。
    /// 所以吞掉异常并留一条日志。
    /// </remarks>
    private async Task RecordPendingReleaseAsync(StockOperation operation, string error, CancellationToken ct)
    {
        try
        {
            await _stocks.AddPendingReleaseAsync(new PendingStockRelease
            {
                BizNo = operation.BizNo,
                SkuId = operation.SkuId,
                Quantity = operation.Quantity,
                Reason = operation.Remark,
                LastError = error.Length > 500 ? error[..500] : error,
                NextRetryAt = DateTime.UtcNow,
                PlatformId = operation.PlatformId,
                MerchantId = operation.MerchantId
            }, ct).ConfigureAwait(false);
        }
        catch (Exception inner) when (inner is not OperationCanceledException)
        {
            // 连补偿记录都写不进去，说明数据库已经出问题了。这里绝不能再抛——
            // 抛出去会把「释放失败」这个真正的根因盖掉，而那才是排查的起点
            _logger.LogError(inner,
                "库存释放失败且补偿记录也写不进去：{BizNo} {SkuId} × {Qty}",
                operation.BizNo, operation.SkuId, operation.Quantity);
        }
    }
}

/// <summary>初始化库存处理器。</summary>
public sealed class InitStockHandler : IRequestHandler<InitStockCommand, ApiResponse<StockChangeResult>>
{
    private readonly IStockRepository _stocks;

    /// <summary>构造处理器。</summary>
    /// <param name="stocks">库存仓储。</param>
    public InitStockHandler(IStockRepository stocks) => _stocks = stocks;

    /// <summary>执行初始化。</summary>
    /// <param name="request">初始化命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回初始库存。</returns>
    public async Task<ApiResponse<StockChangeResult>> Handle(InitStockCommand request, CancellationToken ct)
    {
        var ctx = TenantContextHolder.Current;
        var operation = new StockOperation(
            request.SkuId, StockActions.Init, request.Quantity,
            request.BizNo, "商品创建时初始化", request.PlatformId, request.MerchantId);

        var outcome = await _stocks.InitAsync(
            operation, request.ProductName, request.SkuSpecText,
            request.WarnThreshold, ctx.UserId, ctx.UserName, ct);

        var result = new StockChangeResult(
            request.SkuId, outcome.Available, outcome.Locked, outcome.Deducted, outcome.AlreadyApplied);

        return ApiResults.Ok(result, outcome.AlreadyApplied ? "库存已初始化，返回现有值" : "初始化成功");
    }
}

/// <summary>后台手工调整库存处理器。</summary>
public sealed class AdjustStockHandler : IRequestHandler<AdjustStockCommand, ApiResponse<StockChangeResult>>
{
    private readonly IStockRepository _stocks;

    /// <summary>构造处理器。</summary>
    /// <param name="stocks">库存仓储。</param>
    public AdjustStockHandler(IStockRepository stocks) => _stocks = stocks;

    /// <summary>执行调整。</summary>
    /// <param name="request">调整命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回调整后的库存；调成负数返回 400。</returns>
    /// <remarks>
    /// 提交的是<b>调整量</b>（+10 / -5），不是最终值（改成 20）。
    /// 差别在于并发：两个运营同时打开商品页，A 想调成 20、B 提交「+5」，
    /// 用调整量的话结果是 25（各自的意图都保住了），用最终值的话后提交的会把前一个覆盖掉。
    /// </remarks>
    public async Task<ApiResponse<StockChangeResult>> Handle(AdjustStockCommand request, CancellationToken ct)
    {
        if (request.AvailableAdjust == 0)
        {
            return ApiResults.Fail<StockChangeResult>(BaseApiResponseCode.BadRequest, "调整量不能为 0");
        }

        var ctx = TenantContextHolder.Current;

        // 每次调整一个独立的 BizNo：手工调整没有外部单号可依，
        // 用雪花 Id 保证「每次点击都记一条流水」，而不是被幂等键合并掉。
        var bizNo = $"ADJ-{SnowflakeId.NewId()}";
        var operation = new StockOperation(
            request.SkuId, StockActions.Adjust, request.AvailableAdjust, bizNo,
            request.Remark, ctx.PlatformId, ctx.MerchantId);

        var outcome = await _stocks.ApplyAsync(operation, ctx.UserId, ctx.UserName, ct);

        if (!outcome.Succeeded)
        {
            // 库存不足单独用 4001，调用方（尤其下单链路）要能把「货不够」与
            // 「库存记录不存在 / 请求不合法」区分开：前者是正常业务提示，
            // 后者是数据或程序问题，混成一个码会让下单把两种故障都报成「库存不足」。
            var code = outcome.Failure == StockApplyFailure.Shortage
                ? BaseApiResponseCode.StockNotEnough
                : BaseApiResponseCode.BusinessError;

            return ApiResults.Fail<StockChangeResult>(code, outcome.Error);
        }

        var result = new StockChangeResult(
            request.SkuId, outcome.Available, outcome.Locked, outcome.Deducted, false);

        return ApiResults.Ok(result, "调整成功");
    }
}

/// <summary>分页查询库存处理器。</summary>
public sealed class QueryStocksHandler : IRequestHandler<QueryStocksCommand, ApiResponse<List<StockItem>>>
{
    private readonly IStockRepository _stocks;

    /// <summary>构造处理器。</summary>
    /// <param name="stocks">库存仓储。</param>
    public QueryStocksHandler(IStockRepository stocks) => _stocks = stocks;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>库存列表。</returns>
    public async Task<ApiResponse<List<StockItem>>> Handle(QueryStocksCommand request, CancellationToken ct)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var (items, _) = await _stocks.QueryPagedAsync(page, pageSize, request.Keyword, request.LowStockOnly, ct);

        var list = items.Select(a => new StockItem(
            a.Id.ToString(), a.SkuId, a.ProductName, a.SkuSpecText,
            a.Available, a.Locked, a.Deducted, a.WarnThreshold, a.IsLowStock,
            a.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty)).ToList();

        return ApiResults.Ok(list);
    }
}

/// <summary>查询库存流水处理器。</summary>
public sealed class QueryStockFlowsHandler : IRequestHandler<QueryStockFlowsCommand, ApiResponse<List<StockFlowItem>>>
{
    private readonly IStockRepository _stocks;

    /// <summary>构造处理器。</summary>
    /// <param name="stocks">库存仓储。</param>
    public QueryStockFlowsHandler(IStockRepository stocks) => _stocks = stocks;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>流水列表。</returns>
    public async Task<ApiResponse<List<StockFlowItem>>> Handle(QueryStockFlowsCommand request, CancellationToken ct)
    {
        var flows = await _stocks.GetFlowsAsync(request.SkuId, request.Limit, ct);

        var list = flows.Select(a => new StockFlowItem(
            a.Id.ToString(), a.BizNo, a.SkuId, a.Action, a.Quantity,
            a.BeforeAvailable, a.AfterAvailable,
            a.BeforeLocked, a.AfterLocked,
            a.BeforeDeducted, a.AfterDeducted,
            a.Remark, a.OperationName,
            a.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"))).ToList();

        return ApiResults.Ok(list);
    }
}
/// <summary>按 SKU Id 集合批量取库存的处理器。</summary>
/// <remarks>
/// 订单服务下单前要一次性确认多个 SKU 的库存。如果让它分多次调 List，
/// 订单服务要么多几次往返，要么自己拼——两种都比「一次查一批」差。
/// </remarks>
public sealed class QueryStocksByIdsHandler : IRequestHandler<QueryStocksByIdsCommand, ApiResponse<List<StockItem>>>
{
    private readonly IStockRepository _stocks;

    /// <summary>构造处理器。</summary>
    /// <param name="stocks">库存仓储。</param>
    public QueryStocksByIdsHandler(IStockRepository stocks) => _stocks = stocks;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的库存记录；没命中的 SKU 不会出现在结果里。</returns>
    public async Task<ApiResponse<List<StockItem>>> Handle(QueryStocksByIdsCommand request, CancellationToken ct)
    {
        var rows = await _stocks.GetBySkuIdsAsync(request.SkuIds, ct);

        var list = rows.Select(a => new StockItem(
            a.Id.ToString(), a.SkuId, a.ProductName, a.SkuSpecText,
            a.Available, a.Locked, a.Deducted, a.WarnThreshold, a.IsLowStock,
            a.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty)).ToList();

        return ApiResults.Ok(list);
    }
}
