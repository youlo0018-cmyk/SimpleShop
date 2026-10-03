using Collaboration.Domain.Common;
using MediatR;
using MarketingService.Application.Services;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MarketingService.Application.Features.Seckill;

/// <summary>抢购处理器。</summary>
/// <remarks>
/// <para><b>防超卖靠三层，缺一层都会真的卖超</b>：</para>
/// <list type="number">
/// <item><b>Redis 原子预扣</b> <c>DECBY seckill:stock:{itemId} 1</c>：
/// 抢在所有 DB 操作之前，把绝大多数并发挡在门外，返回值为负说明已抢完。
/// 之所以放最前，是因为它是唯一能把「几万个并发请求」压到「几个」的一层。</item>
/// <item><b>数据库唯一索引</b> <c>uk_seckill_grab_biz</c>（<c>{itemId}:{customerId}</c>）：
/// 限购的最终防线。Redis 记的东西会丢（重启、过期、故障迁移），
/// 而限购额度<b>是钱</b>，一个人抢走整场就是直接损失。</item>
/// <item><b>条件更新</b> <c>TryIncreaseSold</c>（<c>sold + qty &lt;= stock</c>）：
/// 与数据库上的 <c>CHECK (sold_count &lt;= seckill_stock)</c> 一起兜底。</item>
/// </list>
///
/// <para><b>与规格的偏离（已知且有意）</b>：BUSINESS.md 12.5 要求「返回 requestId → 发 MQ → 异步下单 → 轮询」。
/// 本项目目前<b>没有任何服务真正收发 RabbitMQ</b>（只有配置项），从零建消息中间件是另一个独立任务。
/// 这里改成<b>同步下单</b>，但**保留了 requestId 与轮询接口的形状**——
/// 将来接上 MQ 时只需把「同步调 OrderService」换成「发消息」，前端一行都不用改。</para>
/// </remarks>
public sealed class GrabSeckillHandler
    : IRequestHandler<GrabSeckillCommand, ApiResponse<GrabResultDto>>
{
    /// <summary>Redis 里秒杀余量的键前缀。</summary>
    private const string StockKeyPrefix = "seckill:stock:";

    private readonly ISeckillRepository _seckill;
    private readonly IOrderPort _orders;
    private readonly IDatabase _redis;
    private readonly ILogger<GrabSeckillHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    /// <param name="orders">订单服务端口。</param>
    /// <param name="redis">Redis 数据库句柄。</param>
    /// <param name="logger">日志器。</param>
    public GrabSeckillHandler(
        ISeckillRepository seckill, IOrderPort orders,
        IDatabase redis, ILogger<GrabSeckillHandler> logger)
    {
        _seckill = seckill;
        _orders = orders;
        _redis = redis;
        _logger = logger;
    }

    /// <summary>执行抢购。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>抢购结果。</returns>
    public async Task<ApiResponse<GrabResultDto>> Handle(GrabSeckillCommand request, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var bizNo = $"{request.ItemId}:{request.CustomerId}";

        var item = await _seckill.GetItemAsync(request.ItemId, ct).ConfigureAwait(false);
        if (item is null || item.Status != SeckillItemStatuses.Enabled)
        {
            return Reject(requestId, SeckillGrabResults.NotOnSale, "该商品不在抢购中");
        }

        var session = await _seckill.GetSessionAsync(item.SessionId, ct).ConfigureAwait(false);
        if (session is null || session.Status != SeckillSessionStatuses.Running)
        {
            return Reject(requestId, SeckillGrabResults.NotOnSale, "场次不在抢购中");
        }

        var now = DateTime.UtcNow;
        if (now < session.StartTime || now > session.EndTime)
        {
            return Reject(requestId, SeckillGrabResults.NotOnSale, "不在抢购时间内");
        }

        // ---- ① Redis 原子预扣 ----
        var key = StockKeyPrefix + request.ItemId;

        long left;
        try
        {
            left = await _redis.StringDecrementAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Redis 不可用就**不放行**：宁可这一场一个都卖不出去，也不能在没有原子预扣的情况下超卖
            _logger.LogError(ex, "秒杀 Redis 预扣失败，商品 {ItemId} 本次拒绝", request.ItemId);
            return Reject(requestId, SeckillGrabResults.OrderFailed, "抢购服务繁忙，请稍后重试");
        }

        if (left < 0)
        {
            // 已抢完：把自己这一份还回去，否则库存会越扣越少
            await _redis.StringIncrementAsync(key).ConfigureAwait(false);
            return Reject(requestId, SeckillGrabResults.SoldOut, "已抢完");
        }

        // ---- ② 限购（数据库唯一索引兜底）----
        SeckillGrab grab;
        try
        {
            grab = new SeckillGrab
            {
                PlatformId = session.PlatformId,
                MerchantId = session.MerchantId,
                RequestId = requestId,
                BizNo = bizNo,
                SessionId = session.Id,
                ItemId = item.Id,
                CustomerId = request.CustomerId,
                Quantity = 1,
                ResultStatus = SeckillGrabResults.Processing
            };

            await _seckill.InsertGrabAsync(grab, ct).ConfigureAwait(false);
        }
        catch (DuplicateGrabException)
        {
            await _redis.StringIncrementAsync(key).ConfigureAwait(false);
            _logger.LogInformation("客户 {CustomerId} 重复抢购商品 {ItemId}，已被限购拦下", request.CustomerId, request.ItemId);
            return Reject(requestId, SeckillGrabResults.LimitExceeded, "每人每场次限购 1 件");
        }

        // ---- ③ 下单 ----
        var result = await _orders.CreateSeckillOrderAsync(new SeckillOrderRequest(
            request.CustomerId, session.PlatformId, session.MerchantId,
            bizNo,                                  // 幂等键 = 限购业务单号，重复提交拿到同一张单
            request.ReceiverName, request.ReceiverPhone, request.ReceiverAddress,
            item.SpuId, item.SkuId, 1,
            item.SeckillPrice, item.ProductName, item.SkuSpecText, item.DeliveryType,
            request.CouponId, request.PointsToUse, 0m, $"秒杀场次 {session.Id}"), ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // 下单失败：Redis 那一份还回去，否则这场会凭空少货。
            // sold_count 这里**不用动**：它是在下单成功之后才加的，此时还没加过。
            await _redis.StringIncrementAsync(key).ConfigureAwait(false);

            grab.ResultStatus = SeckillGrabResults.OrderFailed;
            grab.ResultMessage = result.Message;
            await _seckill.UpdateGrabAsync(grab, ct).ConfigureAwait(false);

            _logger.LogWarning("秒杀下单失败，商品 {ItemId} 客户 {CustomerId}：{Message}",
                item.Id, request.CustomerId, result.Message);

            return Reject(requestId, SeckillGrabResults.OrderFailed, result.Message);
        }

        // ---- ④ 记账：sold_count + 1（条件更新）----
        var increased = await _seckill.TryIncreaseSoldAsync(item.Id, 1, ct).ConfigureAwait(false);
        if (increased == 0)
        {
            // Redis 放过了但 DB 说满了：两者不一致（Redis 被清过 / 被人改过）。
            // 订单已经创建，不能撤销，只能记一条 Error 让运维修库存。
            _logger.LogError(
                "秒杀记账失败：商品 {ItemId} sold_count 已达上限，但 Redis 预扣放行了。订单 {OrderNo} 已创建，需人工核对库存。",
                item.Id, result.OrderNo);
        }

        grab.ResultStatus = SeckillGrabResults.Success;
        grab.ResultMessage = "抢购成功";
        grab.OrderId = result.OrderId;
        grab.OrderNo = result.OrderNo;
        await _seckill.UpdateGrabAsync(grab, ct).ConfigureAwait(false);

        return ApiResults.Ok(new GrabResultDto(
            requestId, SeckillGrabResults.Success, "抢购成功", result.OrderId, result.OrderNo));
    }

    /// <summary>构造一个「未下单」的失败结果。</summary>
    /// <param name="requestId">请求 Id。</param>
    /// <param name="status">结果码。</param>
    /// <param name="message">提示文案。</param>
    private static ApiResponse<GrabResultDto> Reject(string requestId, int status, string message)
        => ApiResults.Ok(new GrabResultDto(requestId, status, message, 0, string.Empty), message);
}