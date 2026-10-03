using Collaboration.Domain.Common;
using MediatR;
using MarketingService.Application.Services;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;
using Microsoft.Extensions.Logging;

namespace MarketingService.Application.Features.Seckill;

/// <summary>场次状态的中文名。</summary>
public static class SeckillNames
{
    /// <summary>场次状态中文名。</summary>
    /// <param name="status">场次状态。</param>
    /// <returns>中文名。</returns>
    public static string StatusName(int status) => status switch
    {
        SeckillSessionStatuses.NotStarted => "未开始",
        SeckillSessionStatuses.Running => "进行中",
        SeckillSessionStatuses.Ended => "已结束",
        SeckillSessionStatuses.Cancelled => "已取消",
        _ => "未知状态"
    };
}

/// <summary>新建场次处理器。</summary>
public sealed class CreateSessionHandler : IRequestHandler<CreateSessionCommand, ApiResponse<long>>
{
    private readonly ISeckillRepository _seckill;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    public CreateSessionHandler(ISeckillRepository seckill) => _seckill = seckill;

    /// <summary>执行新建。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回场次 Id。</returns>
    /// <remarks>新建一律是「未开始」且<b>库存未划出</b>——先把商品配齐，最后一次性发布。</remarks>
    public async Task<ApiResponse<long>> Handle(CreateSessionCommand request, CancellationToken ct)
    {
        var session = new SeckillSession
        {
            PlatformId = request.PlatformId,
            MerchantId = request.MerchantId,
            SessionName = request.SessionName.Trim(),
            StartTime = ToUtc(request.StartTime),
            EndTime = ToUtc(request.EndTime),
            Status = SeckillSessionStatuses.NotStarted,
            StockTransferred = false,
            SortOrder = request.SortOrder
        };

        var id = await _seckill.InsertSessionAsync(session, ct);
        return ApiResults.Ok(id, "场次创建成功");
    }

    internal static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

/// <summary>编辑场次处理器。</summary>
public sealed class UpdateSessionHandler : IRequestHandler<UpdateSessionCommand, ApiResponse>
{
    private readonly ISeckillRepository _seckill;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    public UpdateSessionHandler(ISeckillRepository seckill) => _seckill = seckill;

    /// <summary>执行编辑。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// <b>库存已划出的场次不允许改时间与商品</b>：库存已经从常规池划走了，
    /// 此时改配置会让「已划出的库存量」与「商品列表」对不上，回补时无从下手。
    /// </remarks>
    public async Task<ApiResponse> Handle(UpdateSessionCommand request, CancellationToken ct)
    {
        var session = await _seckill.GetSessionAsync(request.SessionId, ct);
        if (session is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "场次不存在");

        if (session.StockTransferred)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BusinessError,
                "该场次已发布（库存已划出），不能修改时间。请先中止场次再重新创建。");
        }

        session.SessionName = request.SessionName.Trim();
        session.StartTime = CreateSessionHandler.ToUtc(request.StartTime);
        session.EndTime = CreateSessionHandler.ToUtc(request.EndTime);
        session.SortOrder = request.SortOrder;

        var affected = await _seckill.UpdateSessionAsync(session, ct);
        return affected > 0
            ? ApiResponseFactory.Ok("保存成功")
            : ApiResponseFactory.Fail(BaseApiResponseCode.InternalError, "保存失败，请稍后重试");
    }
}

/// <summary>场次分页处理器。</summary>
public sealed class QuerySessionsHandler : IRequestHandler<QuerySessionsCommand, ApiResponse<SeckillSessionPage>>
{
    private readonly ISeckillRepository _seckill;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    public QuerySessionsHandler(ISeckillRepository seckill) => _seckill = seckill;

    /// <summary>执行分页。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>场次分页结果。</returns>
    public async Task<ApiResponse<SeckillSessionPage>> Handle(QuerySessionsCommand request, CancellationToken ct)
    {
        var (sessions, total) = await _seckill.ListSessionsAsync(
            request.Status, request.Page, request.PageSize, ct);

        // 逐个查商品数会让一屏 20 个场次变成 20 条额外查询。
        // 场次数通常很少，这里为可读性付出一次批量查询是可以接受的取舍
        var items = new List<SessionDto>(sessions.Count);
        foreach (var s in sessions)
        {
            var list = await _seckill.ListItemsAsync(s.Id, ct);
            items.Add(new SessionDto(
                s.Id.ToString(), s.SessionName,
                s.StartTime.ToString("yyyy-MM-dd HH:mm"), s.EndTime.ToString("yyyy-MM-dd HH:mm"),
                s.Status, SeckillNames.StatusName(s.Status), s.StockTransferred, list.Count));
        }

        return ApiResults.Ok(new SeckillSessionPage(items, total, request.Page, request.PageSize));
    }
}

/// <summary>发布场次处理器：<b>把库存从常规池划到秒杀池</b>。</summary>
/// <remarks>
/// 这是整个秒杀里最花钱的一步，所以三条防线都要在：
/// <list type="number">
/// <item><see cref="SeckillSession.StockTransferred"/> 标志：已经划出过的场次直接拒绝，
/// 不给「重复点发布」任何机会。</item>
/// <item>业务单号固定为 <c>SKL-RESERVE-{场次}-{SKU}</c>：万一进程在划转中途挂了、
/// 运营重试，库存服务那边会命中幂等，不会扣第二遍。</item>
/// <item>逐个 SKU 独立处理：一个商品库存不足不影响其它商品，
/// 失败清单随结果一起返回，运营知道该把哪个撤掉。</item>
/// </list>
/// </remarks>
public sealed class PublishSessionHandler : IRequestHandler<PublishSessionCommand, ApiResponse<SeckillPublishResult>>
{
    private readonly ISeckillRepository _seckill;
    private readonly IInventoryPort _inventory;
    private readonly ILogger<PublishSessionHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    /// <param name="inventory">库存端口。</param>
    /// <param name="logger">日志器。</param>
    public PublishSessionHandler(ISeckillRepository seckill, IInventoryPort inventory, ILogger<PublishSessionHandler> logger)
    {
        _seckill = seckill;
        _inventory = inventory;
        _logger = logger;
    }

    /// <summary>执行发布。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>划出结果，含失败 SKU 清单。</returns>
    public async Task<ApiResponse<SeckillPublishResult>> Handle(PublishSessionCommand request, CancellationToken ct)
    {
        var session = await _seckill.GetSessionAsync(request.SessionId, ct);
        if (session is null) return ApiResults.Fail<SeckillPublishResult>(BaseApiResponseCode.NotFound, "场次不存在");

        if (session.Status == SeckillSessionStatuses.Cancelled)
        {
            return ApiResults.Fail<SeckillPublishResult>(
                BaseApiResponseCode.BusinessError, "已取消的场次不能发布，请新建场次");
        }

        // 第一道防线：已划出过就不许再划。
        // Force 只用于「上一轮划转中途失败、需要人工重跑」的场景，
        // 而那种情况下的幂等由业务单号兜底，不会真的扣两遍。
        if (session.StockTransferred && !request.Force)
        {
            return ApiResults.Fail<SeckillPublishResult>(
                BaseApiResponseCode.BusinessError,
                "该场次已发布（库存已划出），不能重复发布");
        }

        var items = await _seckill.ListItemsAsync(session.Id, ct);
        if (items.Count == 0)
        {
            return ApiResults.Fail<SeckillPublishResult>(
                BaseApiResponseCode.BusinessError, "该场次还没有配置商品，无法发布");
        }

        var failed = new List<string>();
        var reservedTotal = 0;

        foreach (var item in items)
        {
            if (item.Status != SeckillItemStatuses.Enabled)
            {
                failed.Add($"{item.SkuId}（商品已停用）");
                continue;
            }

            // 第二道防线：业务单号固定，重复调用命中库存侧幂等
            var bizNo = $"SKL-RESERVE-{session.Id}-{item.SkuId}";
            try
            {
                var ok = await _inventory.ReserveAsync(item.SkuId, item.SeckillStock, bizNo, ct);
                if (!ok)
                {
                    failed.Add($"{item.SkuId}（常规库存不足 {item.SeckillStock}）");
                    continue;
                }

                reservedTotal += item.SeckillStock;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "场次 {SessionId} 划出 SKU {SkuId} 失败", session.Id, item.SkuId);
                failed.Add($"{item.SkuId}（库存服务异常）");
            }
        }

        await _seckill.SetStockTransferredAsync(session.Id, true, ct).ConfigureAwait(false);
        await _seckill.TrySetSessionStatusAsync(
            session.Id, SeckillSessionStatuses.NotStarted, SeckillSessionStatuses.Running, ct).ConfigureAwait(false);

        var result = new SeckillPublishResult(session.Id, items.Count, reservedTotal, failed);

        var message = failed.Count == 0
            ? $"发布成功，划出 {reservedTotal} 件库存"
            : $"发布完成，但有 {failed.Count} 个商品未划出库存";

        return ApiResults.Ok(result, message);
    }
}

/// <summary>结束 / 中止场次处理器：<b>把剩余库存回补常规池</b>。</summary>
public sealed class FinishSessionHandler : IRequestHandler<FinishSessionCommand, ApiResponse<SeckillFinishResult>>
{
    private readonly ISeckillRepository _seckill;
    private readonly IInventoryPort _inventory;
    private readonly ILogger<FinishSessionHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    /// <param name="inventory">库存端口。</param>
    /// <param name="logger">日志器。</param>
    public FinishSessionHandler(ISeckillRepository seckill, IInventoryPort inventory, ILogger<FinishSessionHandler> logger)
    {
        _seckill = seckill;
        _inventory = inventory;
        _logger = logger;
    }

    /// <summary>执行结束 / 中止。</summary>
    /// <param name="request">命令，Cancel = true 表示手动中止。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回补结果，含失败 SKU 清单。</returns>
    /// <remarks>
    /// 回补的是 <c>SeckillStock − SoldCount</c>，<b>不是</b> <c>SeckillStock</c>：
    /// 已抢出去的那部分货已经卖给用户了，还回去等于凭空多出库存。
    ///
    /// 状态迁移用条件更新：两个「结束场次」同时点，只有一个能改成功，
    /// 另一个拿到 0 就知道该直接返回，不会回补第二遍。
    /// </remarks>
    public async Task<ApiResponse<SeckillFinishResult>> Handle(FinishSessionCommand request, CancellationToken ct)
    {
        var session = await _seckill.GetSessionAsync(request.SessionId, ct);
        if (session is null) return ApiResults.Fail<SeckillFinishResult>(BaseApiResponseCode.NotFound, "场次不存在");

        var target = request.Cancel
            ? SeckillSessionStatuses.Cancelled
            : SeckillSessionStatuses.Ended;

        if (session.Status == target)
        {
            return ApiResults.Ok(
                new SeckillFinishResult(session.Id, 0, Array.Empty<string>(), session.Status),
                "该场次已是目标状态");
        }

        var changed = await _seckill.TrySetSessionStatusAsync(
            session.Id, session.Status, target, ct).ConfigureAwait(false);

        if (changed == 0)
        {
            // 别人已经改过了。状态已经是目标态就直接认，不要重复回补
            var current = await _seckill.GetSessionAsync(session.Id, ct);
            if (current is not null && current.Status == target && !current.StockTransferred)
            {
                return ApiResults.Ok(
                    new SeckillFinishResult(session.Id, 0, Array.Empty<string>(), current.Status),
                    "该场次已处理完成");
            }

            return ApiResults.Fail<SeckillFinishResult>(
                BaseApiResponseCode.OrderStateInvalid, "场次状态已变更，请刷新后重试");
        }

        if (!session.StockTransferred)
        {
            return ApiResults.Ok(
                new SeckillFinishResult(session.Id, 0, Array.Empty<string>(), target),
                "该场次未发布过，无需回补库存");
        }

        var items = await _seckill.ListItemsAsync(session.Id, ct);
        var failed = new List<string>();
        var released = 0;

        foreach (var item in items)
        {
            var remaining = Math.Max(0, item.SeckillStock - item.SoldCount);
            if (remaining <= 0) continue;

            // 业务单号带上 release 后缀：与划出时不同，所以库存侧不会把它当成同一次操作
            var bizNo = $"SKL-RELEASE-{session.Id}-{item.SkuId}";
            try
            {
                var ok = await _inventory.ReleaseAsync(item.SkuId, remaining, bizNo, ct);
                if (!ok)
                {
                    failed.Add($"{item.SkuId}（回补失败）");
                    continue;
                }

                released += remaining;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "场次 {SessionId} 回补 SKU {SkuId} 失败", session.Id, item.SkuId);
                failed.Add($"{item.SkuId}（库存服务异常）");
            }
        }

        // 回补完成后才清标志：中途挂掉时标志还在，重跑会继续回补（幂等），不会漏
        await _seckill.SetStockTransferredAsync(session.Id, false, ct).ConfigureAwait(false);

        // 注意：不能用 $"...{'中止' if ...}" —— '中止' 是两个字符，不是合法的 char 字面量。
        // 这里先把词算成一个字符串变量，插值里直接引用
        var word = request.Cancel ? "中止" : "结束";

        var message = failed.Count == 0
            ? $"场次已{word}，回补 {released} 件库存"
            : $"场次已{word}，但有 {failed.Count} 个商品回补失败，请人工核对";

        return ApiResults.Ok(new SeckillFinishResult(session.Id, released, failed, target), message);
    }
}