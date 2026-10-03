using Collaboration.Domain.Infrastructure;
using Microsoft.Extensions.Logging;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;
using OrderService.Domain.Services;

namespace OrderService.Application;

/// <summary>下单的一行输入。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Quantity">数量。</param>
/// <param name="UnitPrice">SKU 售价快照。</param>
/// <param name="ProductName">商品名快照。</param>
/// <param name="SkuSpecText">规格文本快照。</param>
/// <param name="DeliveryType">配送方式。</param>
public readonly record struct OrderLineRequest(
    long SpuId, long SkuId, int Quantity, decimal UnitPrice,
    string ProductName, string SkuSpecText, int DeliveryType);

/// <summary>下单请求。</summary>
public readonly record struct CreateOrderRequest(
    long CustomerId, long PlatformId, long MerchantId,
    string IdempotencyKey, string ReceiverName, string ReceiverPhone, string ReceiverAddress,
    IReadOnlyList<OrderLineRequest> Lines,
    long CouponId = 0, long PointsToUse = 0, string Remark = "",
    FreightRule Freight = default);

/// <summary>下单编排：①占券 → ②锁积分 → ③锁库存 → ④落单，失败逆序回滚。</summary>
/// <remarks>
/// <para><b>这是全系统最复杂的补偿链路</b>（BUSINESS.md 8.1 链路 7）。
/// 三次跨服务调用任一失败，都必须把已经生效的前一步撤回去；
/// 漏掉任何一步回滚都会留下「积分冻结了但订单没有」这类资损。</para>
///
/// <para>回滚必须<b>逆序</b>且<b>尽力而为</b>：
/// 逆序是因为 ③ 失败时 ② 已经生效，先退 ② 再退 ① 才对；
/// 尽力而为是因为回滚本身也可能失败，那时不能把异常抛出去覆盖掉原始错误——
/// 原始错误才是排查的起点，回滚失败另记日志。</para>
///
/// <para>③ 锁库存要<b>逐个 SKU</b> 处理：第 3 个 SKU 失败时，前两个已经锁上的必须释放。
/// 这是最容易漏的一处——把整个订单的库存当成一次调用，
/// 就不会知道哪些 SKU 已经锁成功了。</para>
/// </remarks>
public sealed class OrderCreator
{
    private readonly ICouponPort _coupons;
    private readonly IPointPort _points;
    private readonly IInventoryPort _inventory;
    private readonly IOrderStore _store;
    private readonly ILogger<OrderCreator> _logger;

    /// <summary>构造编排器。</summary>
    /// <param name="coupons">营销端口。</param>
    /// <param name="points">积分端口。</param>
    /// <param name="inventory">库存端口。</param>
    /// <param name="store">落单端口。</param>
    /// <param name="logger">日志器。</param>
    public OrderCreator(
        ICouponPort coupons, IPointPort points, IInventoryPort inventory,
        IOrderStore store, ILogger<OrderCreator> logger)
    {
        _coupons = coupons;
        _points = points;
        _inventory = inventory;
        _store = store;
        _logger = logger;
    }

    /// <summary>创建订单。</summary>
    /// <param name="request">下单请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下单结果，含失败步骤序号。</returns>
    public async Task<OrderCreateOutcome> CreateAsync(CreateOrderRequest request, CancellationToken ct = default)
    {
        if (request.Lines.Count == 0)
        {
            return OrderCreateOutcome.Fail(0, "订单不能没有任何商品行");
        }

        // ---- 幂等：同一个客户 + 同一个键只允许一张单 ----
        // 必须在任何占用动作**之前**查。否则重复请求会先把券/积分/库存占一遍才发现单已经下过。
        var existing = await _store.FindByIdempotencyKeyAsync(request.CustomerId, request.IdempotencyKey, ct);
        if (existing is not null)
        {
            _logger.LogInformation("命中下单幂等：客户 {CustomerId} 键 {Key} → 订单 {OrderNo}",
                request.CustomerId, request.IdempotencyKey, existing.OrderNo);
            return OrderCreateOutcome.Ok(existing.Id, existing.OrderNo, alreadyCreated: true);
        }

        var orderNo = NewOrderNo();
        var lines = request.Lines
            .Select(a => new OrderLineInput(a.SkuId, a.Quantity, a.UnitPrice))
            .ToArray();

        // ---------- ① 营销占券 ----------
        long couponId = 0;
        decimal couponDiscount = 0m;
        try
        {
            var occupy = await _coupons.OccupyAsync(request.CustomerId, orderNo, request.CouponId, lines, ct);
            couponId = occupy.CouponId;
            couponDiscount = occupy.Discount;
        }
        catch (Exception ex)
        {
            // ① 就失败了，前面的步骤不存在，不用回滚
            _logger.LogError(ex, "① 营销占券失败，订单不创建");
            return OrderCreateOutcome.Fail(1, "营销占券失败：" + ex.Message);
        }

        // ---------- ② 锁定积分 ----------
        var pointsUsed = 0L;
        if (request.PointsToUse > 0)
        {
            try
            {
                if (!await _points.LockAsync(request.CustomerId, orderNo, request.PointsToUse, ct))
                {
                    _logger.LogWarning("② 锁定积分失败（余额不足），回滚 ①");
                    await RollbackCouponAsync(request.CustomerId, orderNo, couponId);
                    return OrderCreateOutcome.Fail(2, "可用积分不足");
                }

                pointsUsed = request.PointsToUse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "② 锁定积分异常，回滚 ①");
                await RollbackCouponAsync(request.CustomerId, orderNo, couponId);
                return OrderCreateOutcome.Fail(2, "锁定积分失败：" + ex.Message);
            }
        }

        // ---------- ③ 锁定库存（逐个 SKU） ----------
        var lockedSkus = new List<(long SkuId, int Quantity)>();
        try
        {
            foreach (var line in request.Lines)
            {
                var bizNo = $"{orderNo}:{line.SkuId}";
                if (!await _inventory.LockAsync(line.SkuId, line.Quantity, bizNo, ct))
                {
                    _logger.LogWarning("③ 锁定库存失败（SKU {SkuId} 不足），回滚 ③已锁部分 → ② → ①", line.SkuId);

                    await RollbackInventoryAsync(orderNo, lockedSkus);
                    await RollbackPointsAsync(request.CustomerId, orderNo, pointsUsed);
                    await RollbackCouponAsync(request.CustomerId, orderNo, couponId);

                    return OrderCreateOutcome.Fail(3, $"SKU {line.SkuId} 可用库存不足");
                }

                lockedSkus.Add((line.SkuId, line.Quantity));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "③ 锁定库存异常，逆序回滚");

            await RollbackInventoryAsync(orderNo, lockedSkus);
            await RollbackPointsAsync(request.CustomerId, orderNo, pointsUsed);
            await RollbackCouponAsync(request.CustomerId, orderNo, couponId);

            return OrderCreateOutcome.Fail(3, "锁定库存失败：" + ex.Message);
        }

        // ---------- ④ 落单 ----------
        try
        {
            var amount = OrderAmountCalculator.Calculate(
                lines,
                OrderAmountCalculator.AllocateCouponDiscount(lines, couponDiscount),
                new decimal[lines.Length],   // 活动优惠尚未落地，先全 0
                request.Freight,
                pointsUsed);

            var order = new Order
            {
                OrderNo = orderNo,
                CustomerId = request.CustomerId,
                PlatformId = request.PlatformId,
                MerchantId = request.MerchantId,
                Status = amount.PayableAmount == 0m
                    ? OrderStatuses.PendingShipment    // 实付 0 元直接跳 20，跳过支付
                    : OrderStatuses.PendingPayment,
                GoodsTotal = amount.GoodsTotal,
                Freight = amount.Freight,
                PointsDeduction = amount.PointsDeduction,
                PayableAmount = amount.PayableAmount,
                PointsUsed = pointsUsed,
                CouponId = couponId,
                CouponDiscount = couponDiscount,
                ReceiverName = request.ReceiverName,
                ReceiverPhone = request.ReceiverPhone,
                ReceiverAddress = request.ReceiverAddress,
                IdempotencyKey = request.IdempotencyKey,
                Remark = request.Remark
            };

            var items = request.Lines
                .Select((line, i) => new OrderItem
                {
                    OrderNo = orderNo,
                    SpuId = line.SpuId,
                    SkuId = line.SkuId,
                    ProductName = line.ProductName,
                    SkuSpecText = line.SkuSpecText,
                    Price = line.UnitPrice,
                    Quantity = line.Quantity,
                    OriginalAmount = amount.Lines[i].OriginalAmount,
                    ActivityDiscount = amount.Lines[i].ActivityDiscount,
                    CouponDiscount = amount.Lines[i].CouponDiscount,
                    PayableAmount = amount.Lines[i].PayableAmount,
                    DeliveryType = line.DeliveryType
                })
                .ToList();

            var orderId = await _store.SaveAsync(order, items, ct);
            _logger.LogInformation("下单成功 {OrderNo}，实付 {Amount}", orderNo, amount.PayableAmount);

            return OrderCreateOutcome.Ok(orderId, orderNo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "④ 落单失败，逆序回滚 ③ → ② → ①");

            await RollbackInventoryAsync(orderNo, lockedSkus);
            await RollbackPointsAsync(request.CustomerId, orderNo, pointsUsed);
            await RollbackCouponAsync(request.CustomerId, orderNo, couponId);

            return OrderCreateOutcome.Fail(4, "创建订单失败：" + ex.Message);
        }
    }

    /// <summary>逆序回滚的第一步：释放已锁库存。</summary>
    private async Task RollbackInventoryAsync(string orderNo, List<(long SkuId, int Quantity)> locked)
    {
        foreach (var (skuId, quantity) in locked)
        {
            try
            {
                await _inventory.ReleaseAsync(skuId, quantity, $"{orderNo}:{skuId}");
            }
            catch (Exception ex)
            {
                // 回滚失败不能覆盖原始错误，另记一条以便补偿任务兜底
                _logger.LogError(ex, "回滚释放库存失败：订单 {OrderNo} SKU {SkuId}，需人工或定时任务补偿", orderNo, skuId);
            }
        }
    }

    /// <summary>回滚解冻积分。</summary>
    private async Task RollbackPointsAsync(long customerId, string orderNo, long pointsUsed)
    {
        if (pointsUsed <= 0) return;

        try
        {
            await _points.UnfreezeAsync(customerId, orderNo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "回滚解冻积分失败：订单 {OrderNo}，需补偿", orderNo);
        }
    }

    /// <summary>回滚券占用。没占券就不必退。</summary>
    private async Task RollbackCouponAsync(long customerId, string orderNo, long couponId)
    {
        if (couponId <= 0) return;

        try
        {
            await _coupons.ReleaseAsync(customerId, orderNo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "回滚券占用失败：订单 {OrderNo}，需补偿", orderNo);
        }
    }

    /// <summary>生成订单号。</summary>
    /// <returns>订单号。</returns>
    /// <remarks>时间戳保证可读性，随机段保证同一秒内并发下单不撞号（唯一索引兜底）。</remarks>
    private static string NewOrderNo()
        => $"{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.Next(100000, 999999)}";
}