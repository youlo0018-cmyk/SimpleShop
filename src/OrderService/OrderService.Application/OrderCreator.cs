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
/// <param name="SourceType">
/// 来源类型，见 <see cref="OrderSourceTypes"/>。秒杀行传 <see cref="OrderSourceTypes.Seckill"/>，
/// 支付收尾据此跳过库存扣减（它的库存在发布场次时已划走）。
/// </param>
public readonly record struct OrderLineRequest(
    long SpuId, long SkuId, int Quantity, decimal UnitPrice,
    string ProductName, string SkuSpecText, int DeliveryType,
    int SourceType = OrderSourceTypes.Normal);

/// <summary>下单请求。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="PlatformId">平台 Id。</param>
/// <param name="MerchantId">商户 Id，0 表示平台自营。</param>
/// <param name="IdempotencyKey">幂等键。同一个客户 + 同一个键只会产出一张订单。</param>
/// <param name="ReceiverName">收货人姓名。</param>
/// <param name="ReceiverPhone">收货电话。</param>
/// <param name="ReceiverAddress">收货地址。</param>
/// <param name="Lines">订单行。</param>
/// <param name="CouponId">使用的用户券 Id，0 表示不使用券。</param>
/// <param name="PointsToUse">抵扣积分数，0 表示不用积分。</param>
/// <param name="Remark">备注。</param>
/// <param name="Freight">运费规则。</param>
/// <param name="InventoryPreDeducted">
/// <b>库存是否已在别处预扣走。</b>秒杀单必须传 true。
/// </param>
/// <param name="CustomerNo">客户唯一编码，写进订单做快照。后台按它检索订单而不是按手机号，客户换号后历史订单仍能查到。</param>
/// <remarks>
/// 🔴 这是秒杀单唯一的关键开关：秒杀的货在**发布场次时**就从常规库存划走了，
/// 再走一次「锁常规库存」等于锁走第二份，直接超卖。
/// 所以秒杀单传 true 时，③ 整步跳过、支付收尾也跳过扣减。
///
/// 代价是：**幂等责任落到调用方**——既然下单这步不校验库存，
/// 调用方必须自己保证「扣减库存」与「下单」之间不会重复扣（靠限购的幂等键）。
/// 这一点在秒杀侧靠 `{itemId}:{customerId}` 唯一索引保证。
/// </remarks>
public readonly record struct CreateOrderRequest(
    long CustomerId, long PlatformId, long MerchantId,
    string IdempotencyKey, string ReceiverName, string ReceiverPhone, string ReceiverAddress,
    IReadOnlyList<OrderLineRequest> Lines,
    long CouponId = 0, long PointsToUse = 0, string Remark = "",
    FreightRule Freight = default,
    bool InventoryPreDeducted = false,
    string CustomerNo = "");

/// <summary>下单编排：加客户锁 → ①占券 → ②锁积分 → ③锁库存 → ④落单，失败逆序回滚。</summary>
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
///
/// <para><b>客户锁必须包住整条链路（含幂等查询）</b>：只锁后半段没用，
/// 两个并发请求仍会双双通过幂等查询。所以锁在方法最外层，释放用 <c>await using</c>。</para>
/// </remarks>
public sealed class OrderCreator
{
    /// <summary>等锁的上限。超过就判定为「同一客户在狂点提交」，直接拒。</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    /// <summary>锁持有上限。够跑完四次跨服务调用即可，不宜过大。</summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(30);

    private readonly ICouponPort _coupons;
    private readonly IPointPort _points;
    private readonly IInventoryPort _inventory;
    private readonly IActivityPort _activities;
    private readonly IProductPort _products;
    private readonly IPlatformPort _platforms;
    private readonly IOrderStore _store;
    private readonly IOrderCreateLock _createLock;
    private readonly OrderPaymentCompleter _completer;
    private readonly ILogger<OrderCreator> _logger;

    /// <summary>构造编排器。</summary>
    /// <param name="coupons">营销端口。</param>
    /// <param name="points">积分端口。</param>
    /// <param name="inventory">库存端口。</param>
    /// <param name="activities">活动优惠试算端口，用于把结算页看到的满减落到订单上。</param>
    /// <param name="products">商品端口，用于回查 SKU 的权威售价与可售状态。</param>
    /// <param name="platforms">平台端口，用于读平台级运费配置。</param>
    /// <param name="store">落单端口。</param>
    /// <param name="createLock">客户级下单锁。</param>
    /// <param name="completer">支付收尾服务，用于实付 0 元的单在下单当场结清占用。</param>
    /// <param name="logger">日志器。</param>
    public OrderCreator(
        ICouponPort coupons, IPointPort points, IInventoryPort inventory,
        IActivityPort activities, IProductPort products, IPlatformPort platforms,
        IOrderStore store, IOrderCreateLock createLock,
        OrderPaymentCompleter completer, ILogger<OrderCreator> logger)
    {
        _coupons = coupons;
        _points = points;
        _inventory = inventory;
        _activities = activities;
        _products = products;
        _platforms = platforms;
        _store = store;
        _createLock = createLock;
        _completer = completer;
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

        // await using：无论中途走哪条 return / 抛异常，锁都一定会释放。
        // 写成 try/finally 手工释放很容易在早退分支上漏掉。
        var handle = await _createLock.TryAcquireAsync(request.CustomerId, LockWait, LockTtl, ct);
        if (handle is null)
        {
            _logger.LogWarning("客户 {CustomerId} 的下单锁等待 {Wait} 仍未取得，疑似重复提交", request.CustomerId, LockWait);
            return OrderCreateOutcome.Fail(0, "你有一个订单正在提交中，请稍候再试");
        }

        await using (handle.ConfigureAwait(false))
        {
            return await CreateCoreAsync(request, ct).ConfigureAwait(false);
        }
    }

    /// <summary>持锁后的实际编排。抽出来是为了让锁的生命周期一眼可见。</summary>
    /// <param name="request">下单请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下单结果，含失败步骤序号。</returns>
    private async Task<OrderCreateOutcome> CreateCoreAsync(CreateOrderRequest request, CancellationToken ct)
    {
        // ---- 幂等：同一个客户 + 同一个键只允许一张单 ----
        // 必须在任何占用动作**之前**查。否则重复请求会先把券/积分/库存占一遍才发现单已经下过。
        // 此时已经持有客户锁，所以「查不到」就意味着真的还没下过。
        var existing = await _store.FindByIdempotencyKeyAsync(request.CustomerId, request.IdempotencyKey, ct);
        if (existing is not null)
        {
            _logger.LogInformation("命中下单幂等：客户 {CustomerId} 键 {Key} → 订单 {OrderNo}",
                request.CustomerId, request.IdempotencyKey, existing.OrderNo);
            return OrderCreateOutcome.Ok(existing.Id, existing.OrderNo, alreadyCreated: true);
        }

        var orderNo = NewOrderNo();

        // ---------- ⓪⓪ 回查权威售价与可售状态 ----------
        // 这是下单链路**最关键**的一步：unitPrice 来自客户端，此前全程直接采信，
        // 于是把 25.50 的商品按 0.01 元下单也能成交，整条金额链路由客户端说了算。
        // 这里一律改用商品服务给的售价，并顺带拦住「未审核 / 已下架 / 已停用」的商品。
        var skuIds = request.Lines.Select(a => a.SkuId).Distinct().ToArray();
        var pricing = await _products.GetSkuPricesAsync(skuIds, ct).ConfigureAwait(false);

        var resolved = new List<OrderLineRequest>(request.Lines.Count);
        foreach (var line in request.Lines)
        {
            if (!pricing.TryGetValue(line.SkuId, out var sku))
            {
                _logger.LogError("下单被拒：SKU {SkuId} 查不到或商品服务不可用", line.SkuId);
                return OrderCreateOutcome.Fail(
                    4, $"商品 {line.SkuId} 不存在或暂不可售，请刷新后重试");
            }

            if (!sku.Enabled)
            {
                return OrderCreateOutcome.Fail(4, $"商品规格「{line.SkuId}」已停用");
            }

            if (!sku.SpuApproved)
            {
                return OrderCreateOutcome.Fail(4, $"商品「{line.SkuId}」尚未通过审核");
            }

            if (!sku.SpuOnShelf)
            {
                return OrderCreateOutcome.Fail(4, $"商品「{line.SkuId}」已下架");
            }

            if (line.Quantity <= 0)
            {
                return OrderCreateOutcome.Fail(4, "购买数量必须大于 0");
            }

            // 用权威售价覆盖客户端报的价格。
            // 不因为「报得不一样」就报错 —— 客户端可能拿着旧缓存，
            // 直接纠正即可；只有 SKU 不存在 / 不可售才拒单。
            //
            // ⚠️ 秒杀行例外：它的单价来自场次，不是商品售价，
            // 拿售价覆盖会把秒杀单变成原价单（TEST_CASES API-SEC-002）。
            // 秒杀价的权威性由营销服务保证，这里只校验可售状态。
            var isSeckill = line.SourceType == OrderSourceTypes.Seckill;
            resolved.Add(line with
            {
                UnitPrice = isSeckill ? line.UnitPrice : sku.Price,

                // 配送方式同样以商品服务为准。运费只对「实物快递」收，
                // 而客户端把自提商品报成快递就能凭空多收一笔运费。
                DeliveryType = sku.DeliveryType,
            });
        }

        var amountLines = request.Lines
            .Select((a, i) => new OrderLineInput(
                a.SkuId, a.Quantity, resolved[i].UnitPrice))
            .ToArray();

        // ---------- ⓪⓪⓪ 服务端算运费 ----------
        // 运费是订单金额的一部分，金额不能由客户端说了算（与上面单价同一条道理）。
        // 规则来自 BUSINESS.md 6.2：只对「实物快递」收，金额取平台级配置，
        // 满额（按商品实付）包邮。虚拟商品与自提恒为 0。
        //
        // 刻意**不去读 request.Freight**：前端目前把它硬编码成 0，
        // 采信它等于平台运费永远收不到 —— 后台把运费配成 10 元，顾客照样免运费。
        //
        // 只有含实物快递行时才去读配置：虚拟 / 自提单的运费恒为 0，
        // 没必要为它们多一次跨服务调用，也就不该被商户平台服务的抖动拖住。
        var freightRule = await ResolveFreightRuleAsync(request.PlatformId, resolved, ct)
            .ConfigureAwait(false);

        var couponLines = request.Lines
            .Select((a, i) => new CouponPortLine(
                a.SpuId, a.SkuId,
                OrderAmountCalculator.Round2(resolved[i].UnitPrice * a.Quantity)))
            .ToArray();

        // ---------- ⓪ 活动优惠试算 ----------
        // 刻意放在**占券之前**：活动与券互斥（BUSINESS 11.3），
        // 而占券之后这张券已经锁住、不在「可用券」列表里，
        // 试算就会当客户没用券 → 活动又算一遍 → 券和活动优惠叠加，
        // 结算页算一套、下单算另一套。
        var activityBySku = await _activities.QuoteAsync(
            request.CustomerId, request.PlatformId, 0,
            request.CouponId,
            couponLines.Select(a => (a.SpuId, a.SkuId, a.Amount)).ToArray(),
            ct).ConfigureAwait(false);

        var activityDiscounts = couponLines
            .Select(a => activityBySku.FirstOrDefault(b => b.SkuId == a.SkuId).ActivityDiscount)
            .ToArray();

        // ---------- ① 营销占券 ----------
        //
        // couponId = 0 表示**不使用券**，这里直接跳过占券调用，绝不让服务端替用户挑一张。
        //
        // 为什么不用「0 = 自动选最优券」：BUSINESS.md 7.4 / 用户需求 K9 写得很明确——
        // 「结算页先默认选择最优惠的券并展示其他可用券，如果用户选择其他券就使用其他券」。
        // 也就是说选择权在客户端，服务端再自动挑一次会造成两件糟糕的事：
        //   ① 用户在结算页点了「不使用券」，下单时服务端又给他占上一张，到手价与页面显示不一致；
        //   ② 用户选了 A 券（临期），服务端换成 B 券（面额相同但有效期长），差额虽然为零，
        //      但「我明明选的是那张」这件事没法解释。
        // 「哪张券最优」由 /coupons/Settle 在结算页算好传给这里，服务端不重复这个决策。
        long couponId = 0;
        decimal couponDiscount = 0m;

        if (request.CouponId > 0)
        {
            try
            {
                var occupy = await _coupons.OccupyAsync(request.CustomerId, orderNo, request.CouponId, couponLines, ct);
                couponId = occupy.CouponId;
                couponDiscount = occupy.Discount;

                // 指定了券却没占到（已被别人用掉、已过期、门槛被活动改高了）：
                // 这是正常业务，按没券继续下单，不能把整单判失败。
                if (couponId <= 0)
                {
                    _logger.LogInformation("指定券 {CouponId} 未能占用，本单按不使用券继续", request.CouponId);
                }
            }
            catch (Exception ex)
            {
                // ① 就失败了，前面的步骤不存在，不用回滚
                _logger.LogError(ex, "① 营销占券失败，订单不创建");
                return OrderCreateOutcome.Fail(1, "营销占券失败：" + ex.Message);
            }
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

        // 秒杀单：库存已在发布场次时划走，这里**必须整步跳过**。
        // 再锁一次常规库存等于锁走第二份，秒杀就会超卖——而秒杀超卖是直接的钱。
        if (request.InventoryPreDeducted)
        {
            _logger.LogInformation("③ 跳过锁库存：该单的库存在下单前已被预扣走（秒杀），订单 {OrderNo}", orderNo);
        }
        else
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
                amountLines,
                OrderAmountCalculator.AllocateCouponDiscount(amountLines, couponDiscount),
                activityDiscounts,
                freightRule,
                pointsUsed);

            var order = new Order
            {
                OrderNo = orderNo,
                CustomerId = request.CustomerId,
                CustomerNo = request.CustomerNo,
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
                    // 用权威售价，不是客户端报的那个 ——
                    // 订单行是最长久的对账凭据，这里存错价，后面每一张退款单都会跟着错
                    Price = resolved[i].UnitPrice,
                    Quantity = line.Quantity,
                    OriginalAmount = amount.Lines[i].OriginalAmount,
                    ActivityDiscount = amount.Lines[i].ActivityDiscount,
                    CouponDiscount = amount.Lines[i].CouponDiscount,
                    PayableAmount = amount.Lines[i].PayableAmount,
                    DeliveryType = line.DeliveryType,
                    SourceType = line.SourceType
                })
                .ToList();

            // 用返回的权威实体，而不是自己生成的那个：并发撞幂等键时
            // 落库的是先到的那张单，订单号必须跟着它走。
            var saved = await _store.SaveAsync(order, items, ct);
            var raced = saved.Id != order.Id;

            if (raced)
            {
                // 先到的那张单已经占过券 / 积分 / 库存，本次这些占用等于重复了一次。
                // 幂等键相同意味着是同一个用户的重试，所以要把多占的部分退回去。
                _logger.LogWarning("下单并发撞幂等键，本次占用需回退：请求订单号 {Requested}，实际订单号 {Actual}",
                    orderNo, saved.OrderNo);

                try
                {
                    // 退的时候必须用**本次请求自己生成的** orderNo：
                    // 占用时的幂等键就是拿它拼的，用已存在那张单的号去退会顶不到本次的记录。
                    foreach (var line in request.Lines)
                    {
                        await SafeReleaseInventoryAsync(orderNo, line.SkuId, line.Quantity);
                    }

                    if (pointsUsed > 0) await _points.UnfreezeAsync(request.CustomerId, orderNo);
                    if (couponId > 0) await _coupons.ReleaseAsync(request.CustomerId, orderNo);
                }
                catch (Exception ex)
                {
                    // 这里绝不能让异常冒出去：订单已经存在了（先到的那张），
                    // 一旦冒出去会被外层的 catch 报成「创建订单失败」，
                    // 用户看到失败、手上的单却真实存在，只会再来一次更麻烦。
                    _logger.LogError(ex, "并发下单回退占用失败：订单 {OrderNo}，需补偿任务兜底", orderNo);
                }

                return OrderCreateOutcome.Ok(saved.Id, saved.OrderNo, alreadyCreated: true);
            }

            _logger.LogInformation("下单成功 {OrderNo}，实付 {Amount}", saved.OrderNo, amount.PayableAmount);

            // 实付 0 元的单没有支付这一步，也就<b>没有任何人</b>会来跑支付收尾：
            // 库存会一直锁着、积分会一直冻着、券会一直占着，而用户在订单列表里看到的是「待发货」。
            // 所以这里当场把它结清——和支付成功走的是同一个方法，不会出现两条算法。
            if (amount.PayableAmount == 0m)
            {
                await SettleWithoutPaymentAsync(saved, ct).ConfigureAwait(false);
            }

            return OrderCreateOutcome.Ok(saved.Id, saved.OrderNo);
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

    /// <summary>按平台配置算出本单该收多少运费（BUSINESS.md 6.2）。</summary>
    /// <param name="platformId">平台 Id。</param>
    /// <param name="lines">已用权威数据纠正过的订单行。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>运费规则；无实物快递行时返回全 0。</returns>
    /// <remarks>
    /// <b>刻意忽略 <c>request.Freight</c></b>：它来自客户端，前端目前直接写 0。
    /// 采信它，后台把平台运费配成 10 元也一分钱收不到。
    /// </remarks>
    private async Task<FreightRule> ResolveFreightRuleAsync(
        long platformId, IReadOnlyList<OrderLineRequest> lines, CancellationToken ct)
    {
        // 没有实物快递行 → 运费恒为 0，不去读平台配置。
        // 虚拟商品与自提都是「点了发货/核销就完成」，没有任何物流环节，
        // 让它们为一次用不上的跨服务调用买单没有道理。
        if (lines.All(a => a.DeliveryType != DeliveryTypeIds.PhysicalExpress))
        {
            return new FreightRule(0m, 0m);
        }

        var config = await _platforms.GetShippingConfigAsync(platformId, ct).ConfigureAwait(false);

        _logger.LogInformation("运费按平台配置计算：平台 {PlatformId} 运费 {Fee} 包邮门槛 {Threshold}",
            platformId, config.ShippingFee, config.FreeShippingThreshold);

        return new FreightRule(config.ShippingFee, config.FreeShippingThreshold);
    }

    /// <summary>逆序回滚的第一步：释放已锁库存。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="locked">本单已经锁成功的 SKU 列表。</param>
    /// <returns>异步任务。</returns>
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

    /// <summary>实付 0 元的单当场结清占用。失败只记日志，不影响下单结果。</summary>
    /// <param name="order">刚落库的订单。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 这里<b>不能</b>把异常抛给用户：订单已经落库、用户也确实该拿到这张单，
    /// 报「下单失败」只会让他以为没下成、于是再下一张——那才是真的资损。
    /// 结清失败只留 Error 日志（带订单号），由补偿任务兜底。
    /// </remarks>
    private async Task SettleWithoutPaymentAsync(Order order, CancellationToken ct)
    {
        try
        {
            var settled = await _completer.CompleteAsync(order, ct).ConfigureAwait(false);
            if (!settled.Succeeded)
            {
                _logger.LogError(
                    "实付 0 元的订单 {OrderNo} 下单后结清占用失败（步骤 {Step}）：{Error}，需补偿任务兜底",
                    order.OrderNo, settled.FailedStep, settled.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "实付 0 元的订单 {OrderNo} 下单后结清占用异常，需补偿任务兜底", order.OrderNo);
        }
    }

    /// <summary>并发撞幂等键时的单 SKU 释放，失败只记日志不抛。</summary>
    /// <param name="orderNo">本次请求生成的订单号，占用时的幂等键就是拿它拼的。</param>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="quantity">数量。</param>
    /// <returns>异步任务。</returns>
    private async Task SafeReleaseInventoryAsync(string orderNo, long skuId, int quantity)
    {
        try
        {
            await _inventory.ReleaseAsync(skuId, quantity, $"{orderNo}:{skuId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "并发下单回退库存失败：订单 {OrderNo} SKU {SkuId}，需补偿", orderNo, skuId);
        }
    }

    /// <summary>回滚解冻积分。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号。</param>
    /// <param name="pointsUsed">本单实际冻结的积分数。</param>
    /// <returns>异步任务。</returns>
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
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号。</param>
    /// <param name="couponId">本次占用的券 Id，0 表示没用券。</param>
    /// <returns>异步任务。</returns>
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
