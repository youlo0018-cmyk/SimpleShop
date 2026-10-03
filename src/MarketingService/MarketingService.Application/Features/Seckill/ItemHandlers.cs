using Collaboration.Domain.Common;
using MediatR;
using MarketingService.Application.Services;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;
using MarketingService.Domain.Services;

namespace MarketingService.Application.Features.Seckill;

/// <summary>场次内新增商品处理器。</summary>
public sealed class AddSessionItemHandler : IRequestHandler<AddSessionItemCommand, ApiResponse<long>>
{
    private readonly ISeckillRepository _seckill;
    private readonly IProductPort _products;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    /// <param name="products">商品端口（取 SKU 快照与售价）。</param>
    public AddSessionItemHandler(ISeckillRepository seckill, IProductPort products)
    {
        _seckill = seckill;
        _products = products;
    }

    /// <summary>执行新增。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回商品 Id。</returns>
    /// <remarks>
    /// <b>只能在场次发布前添加</b>：发布时库存已经按当时的商品列表一次性划走了，
    /// 事后再加的商品根本没有对应的划出记录，用户会看到一个抢不了也退不掉的商品。
    /// </remarks>
    public async Task<ApiResponse<long>> Handle(AddSessionItemCommand request, CancellationToken ct)
    {
        var session = await _seckill.GetSessionAsync(request.SessionId, ct);
        if (session is null) return ApiResults.Fail<long>(BaseApiResponseCode.NotFound, "场次不存在");

        if (session.StockTransferred || session.Status != SeckillSessionStatuses.NotStarted)
        {
            return ApiResults.Fail<long>(
                BaseApiResponseCode.BusinessError, "该场次已发布，不能再添加商品");
        }

        var snapshots = await _products.GetSkuSnapshotsAsync([request.SkuId], ct);
        if (!snapshots.TryGetValue(request.SkuId, out var sku))
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.NotFound, "SKU 不存在或已下架");
        }

        // 秒杀价比原价还贵没有意义，而且会让用户对整个价格体系失去信任。
        // 这条比「价格必须大于 0」重要得多
        if (request.SeckillPrice >= sku.Price)
        {
            return ApiResults.Fail<long>(
                BaseApiResponseCode.BusinessError,
                $"秒杀价必须低于商品原价 {sku.Price:0.00} 元");
        }

        var item = new SeckillItem
        {
            PlatformId = session.PlatformId,
            MerchantId = session.MerchantId,
            SessionId = session.Id,
            SpuId = sku.SpuId,
            SkuId = sku.SkuId,
            ProductName = sku.ProductName,
            SkuSpecText = sku.SkuSpecText,
            Image = sku.Image,
            SeckillPrice = CouponCalculator.Round2(request.SeckillPrice),
            OriginalPrice = CouponCalculator.Round2(sku.Price),
            SeckillStock = request.SeckillStock,
            PerUserLimit = request.PerUserLimit,
            SoldCount = 0,
            Status = SeckillItemStatuses.Enabled,
            SortOrder = request.SortOrder
        };

        var id = await _seckill.InsertItemAsync(item, ct);
        return ApiResults.Ok(id, "添加成功");
    }
}

/// <summary>场次商品查询处理器（后台）。</summary>
public sealed class QuerySessionItemsHandler : IRequestHandler<QuerySessionItemsCommand, ApiResponse<List<SessionItemDto>>>
{
    private readonly ISeckillRepository _seckill;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    public QuerySessionItemsHandler(ISeckillRepository seckill) => _seckill = seckill;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>场次商品列表。</returns>
    public async Task<ApiResponse<List<SessionItemDto>>> Handle(
        QuerySessionItemsCommand request, CancellationToken ct)
    {
        var items = await _seckill.ListItemsAsync(request.SessionId, ct);

        var list = items.Select(a => new SessionItemDto(
            a.Id.ToString(), a.SkuId.ToString(), a.ProductName, a.SkuSpecText,
            a.SeckillPrice, a.SeckillStock, a.SoldCount, a.Remaining,
            a.PerUserLimit, a.Status)).ToList();

        return ApiResults.Ok(list);
    }
}

/// <summary>删除场次商品处理器。</summary>
public sealed class DeleteSessionItemHandler : IRequestHandler<DeleteSessionItemCommand, ApiResponse>
{
    private readonly ISeckillRepository _seckill;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    public DeleteSessionItemHandler(ISeckillRepository seckill) => _seckill = seckill;

    /// <summary>执行删除。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(DeleteSessionItemCommand request, CancellationToken ct)
    {
        var item = await _seckill.GetItemAsync(request.ItemId, ct);
        if (item is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "商品不存在");

        var session = await _seckill.GetSessionAsync(item.SessionId, ct);
        if (session is not null && session.StockTransferred)
        {
            // 发布后删商品会让它那部分库存永远回补不了（已经没有对应的划转记录可查了）
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BusinessError,
                "该场次已发布，不能删除商品。请先中止场次让库存回补，再重新配置。");
        }

        var affected = await _seckill.SoftDeleteItemAsync(request.ItemId, ct);
        return affected > 0
            ? ApiResponseFactory.Ok("删除成功")
            : ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "商品不存在");
    }
}

/// <summary>前台场次查询处理器。</summary>
/// <remarks>
/// 倒计时的秒数在<b>服务端</b>算好给前端，而不是把时间戳丢过去让小程序自己减：
/// 小程序设备的本地时间不准，用户改一下系统时间，倒计时就会变成负数或卡住不动。
/// </remarks>
public sealed class QueryPublicSessionsHandler
    : IRequestHandler<QueryPublicSessionsCommand, ApiResponse<List<PublicSessionDto>>>
{
    private readonly ISeckillRepository _seckill;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    public QueryPublicSessionsHandler(ISeckillRepository seckill) => _seckill = seckill;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可抢购的场次与商品。</returns>
    public async Task<ApiResponse<List<PublicSessionDto>>> Handle(
        QueryPublicSessionsCommand request, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;

        var sessions = request.SessionId > 0
            ? (await _seckill.GetSessionAsync(request.SessionId, ct)) is { } s ? [s] : []
            : await _seckill.ListPublicSessionsAsync(nowUtc, request.PlatformId, ct);

        var result = new List<PublicSessionDto>(sessions.Count);

        foreach (var session in sessions)
        {
            // 传了场次 Id 时也要按当前时间过滤：否则可以把「已结束」的场次 Id 硬塞进来
            if (session.EndTime <= nowUtc) continue;

            var items = (await _seckill.ListItemsAsync(session.Id, ct))
                .Where(a => a.Status == SeckillItemStatuses.Enabled)
                .OrderBy(a => a.SortOrder).ThenBy(a => a.Id)
                .ToList();

            // 库存为 0 的商品不必展示：用户点进去发现「已抢完」体验很差
            var dtos = items
                .Where(a => a.Remaining > 0)
                .Select(a => new PublicSeckillItemDto(
                    a.Id.ToString(), a.SpuId.ToString(), a.SkuId.ToString(),
                    a.ProductName, a.SkuSpecText, a.Image,
                    a.OriginalPrice, a.SeckillPrice, a.Remaining, a.PerUserLimit))
                .ToList();

            result.Add(new PublicSessionDto(
                session.Id.ToString(), session.SessionName,
                session.StartTime.ToString("O"), session.EndTime.ToString("O"),
                session.Status,
                SecondsBetween(nowUtc, session.StartTime),
                SecondsBetween(nowUtc, session.EndTime),
                dtos));
        }

        return ApiResults.Ok(result);
    }

    /// <summary>两个时点之间的秒数，已过去算 0。</summary>
    /// <param name="nowUtc">当前时间。</param>
    /// <param name="target">目标时间。</param>
    /// <returns>秒数。</returns>
    private static long SecondsBetween(DateTime nowUtc, DateTime target)
    {
        var seconds = (long)(target - nowUtc).TotalSeconds;
        return seconds > 0 ? seconds : 0;
    }
}