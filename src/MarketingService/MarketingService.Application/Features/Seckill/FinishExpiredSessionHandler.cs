using Collaboration.Domain.Common;
using MarketingService.Application.Services;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;
using MediatR;
using Microsoft.Extensions.Logging;

namespace MarketingService.Application.Features.Seckill;

/// <summary>结束所有「到点但仍在进行中」的秒杀场次（定时任务调用）。</summary>
/// <remarks>
/// 没有这个入口，一个没人手动中止的场次会永远停在「进行中」：
/// 剩余库存永久锁在秒杀池里，常规库存再也回不来，而且没有任何报错——
/// 商品只是「一直缺货」，从现象上完全看不出是这个原因。
/// </remarks>
/// <param name="Limit">单轮最多结束多少个场次。</param>
public record FinishExpiredSessionsCommand(int Limit = 50)
    : IRequest<ApiResponse<FinishExpiredResult>>;

/// <summary>批量结束结果。</summary>
/// <param name="Scanned">扫描到的到期场次数。</param>
/// <param name="Finished">真正结束掉的场次数。</param>
/// <param name="Released">回补的总件数。</param>
/// <param name="FailedItems">回补失败的商品清单。</param>
public sealed record FinishExpiredResult(
    int Scanned, int Finished, int Released, IReadOnlyList<string> FailedItems);

/// <summary>到期场次批量结束处理器。</summary>
public sealed class FinishExpiredSessionsHandler
    : IRequestHandler<FinishExpiredSessionsCommand, ApiResponse<FinishExpiredResult>>
{
    private readonly ISeckillRepository _seckill;
    private readonly SeckillStockReturner _returner;
    private readonly ILogger<FinishExpiredSessionsHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    /// <param name="returner">库存回补器。</param>
    /// <param name="logger">日志器。</param>
    public FinishExpiredSessionsHandler(
        ISeckillRepository seckill,
        SeckillStockReturner returner,
        ILogger<FinishExpiredSessionsHandler> logger)
    {
        _seckill = seckill;
        _returner = returner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ApiResponse<FinishExpiredResult>> Handle(
        FinishExpiredSessionsCommand request, CancellationToken ct)
    {
        var limit = Math.Clamp(request.Limit, 1, 500);

        var expired = await _seckill
            .ListExpiredRunningSessionsAsync(DateTime.UtcNow, limit, ct)
            .ConfigureAwait(false);

        if (expired.Count == 0)
        {
            return ApiResults.Ok(new FinishExpiredResult(0, 0, 0, Array.Empty<string>()));
        }

        var finished = 0;
        var released = 0;
        var failed = new List<string>();

        foreach (var session in expired)
        {
            ct.ThrowIfCancellationRequested();

            var outcome = await FinishOneAsync(session, ct).ConfigureAwait(false);

            if (!outcome.Claimed) continue;

            finished++;
            released += outcome.Released;
            failed.AddRange(outcome.Failed);
        }

        var message = finished == 0
            ? $"扫描 {expired.Count} 个到期场次，均已被其他路径处理"
            : $"结束 {finished} 个到期场次，回补 {released} 件库存"
                + (failed.Count > 0 ? $"，{failed.Count} 项回补失败需人工核对" : string.Empty);

        return ApiResults.Ok(
            new FinishExpiredResult(expired.Count, finished, released, failed), message);
    }

    /// <summary>结束单个场次。</summary>
    /// <param name="session">场次。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>处理结果。</returns>
    /// <remarks>
    /// <b>逐个场次单独 try</b>：一个场次回补失败（库存服务抖动）不能拖垮整轮。
    /// 一起 try 的话，第一个失败就跳出，后面的场次全都结束不掉——
    /// 于是「库存服务挂 5 分钟」会变成「这期间到期的所有场次都漏掉了」。
    /// 漏掉的场次再也不会被扫到（查询条件是「仍在进行中」），库存就永久损失了。
    /// </remarks>
    private async Task<FinishOneOutcome> FinishOneAsync(
        SeckillSession session, CancellationToken ct)
    {
        // 条件更新带上「期望的当前状态 = 进行中」：
        // 运营可能正好在手动中止同一个场次，两条路径撞车时只有一个能改成功，
        // 另一个拿到 0 就直接跳过——否则会回补两遍库存，凭空多出一批货。
        var changed = await _seckill.TrySetSessionStatusAsync(
                session.Id, SeckillSessionStatuses.Running, SeckillSessionStatuses.Ended, ct)
            .ConfigureAwait(false);

        if (changed == 0)
        {
            _logger.LogInformation(
                "场次 {SessionId} 状态已被其他路径改过，跳过自动结束", session.Id);
            return new FinishOneOutcome(false, 0, Array.Empty<string>());
        }

        try
        {
            var outcome = await _returner.ReturnAsync(session, ct).ConfigureAwait(false);
            return new FinishOneOutcome(true, outcome.Released, outcome.Failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 状态已经改成「已结束」，但回补没做完。
            // 这时 StockTransferred 仍然是 true，而下一轮的查询条件是
            // 「仍在进行中」，所以这个场次再也扫不到了——
            // 必须靠库存侧的补偿表兜住，不能就这么算了。
            _logger.LogError(
                ex, "场次 {SessionId} 已置为结束但回补异常，需人工核对库存", session.Id);
            return new FinishOneOutcome(true, 0, new[] { $"{session.Id}（回补异常）" });
        }
    }
}

/// <summary>单个场次的处理结果。</summary>
/// <param name="Claimed">是否真的抢到了结束权（false 表示已被别的路径改掉）。</param>
/// <param name="Released">回补件数。</param>
/// <param name="Failed">失败清单。</param>
internal sealed record FinishOneOutcome(bool Claimed, int Released, IReadOnlyList<string> Failed);
