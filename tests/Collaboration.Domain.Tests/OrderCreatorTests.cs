using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Application;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;
using OrderService.Domain.Services;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>下单编排的单元测试：用假端口在指定步骤注入失败，断言回滚是否干净。</summary>
/// <remarks>
/// 这组用例是本系统最复杂的补偿链路的唯一防线。
/// 「代码顺序看起来对」证明不了回滚正确——必须真的让某一步失败，
/// 然后检查前面已经生效的副作用有没有被撤回去。
/// </remarks>
public class OrderCreatorTests
{
    private const long CustomerId = 5550001L;

    // 默认带一张券（couponId 77）：大多数用例要断言 ① 之后的回滚行为，
    // 不给券的话 ① 整个被跳过，「① 的券有没有被退掉」就没得测了。
    private static CreateOrderRequest Request(
        string key = "idem-1", long couponId = 77, long points = 0, int lineCount = 2)
    {
        var lines = new List<OrderLineRequest>();
        for (var i = 0; i < lineCount; i++)
        {
            lines.Add(new OrderLineRequest(
                SpuId: 100 + i, SkuId: 1000 + i, Quantity: 2, UnitPrice: 25.50m,
                ProductName: $"商品{i}", SkuSpecText: "红色 / M", DeliveryType: 1));
        }

        return new CreateOrderRequest(
            CustomerId, PlatformId: 1, MerchantId: 0,
            IdempotencyKey: key, ReceiverName: "张三", ReceiverPhone: "13800000000",
            ReceiverAddress: "某地", Lines: lines, CouponId: couponId, PointsToUse: points);
    }

    // 收尾服务用**真实**对象配假端口：它只有几十行、且正是要测的逻辑，
    // 换成假实现就等于把「0 元单有没有结清占用」这件事从测试里抹掉了。
    private static OrderCreator Build(
        FakeCouponPort coupons, FakePointPort points, FakeInventoryPort inventory, FakeOrderStore store,
        FakeOrderCreateLock? createLock = null, IProductPort? products = null,
        FakePlatformPort? platforms = null)
    {
        var completer = new OrderPaymentCompleter(
            store, inventory, points, coupons, NullLogger<OrderPaymentCompleter>.Instance);

        return new OrderCreator(
            coupons, points, inventory, new FakeActivityPort(),
            new OrderPricingResolver(
                products ?? new FakeProductPort(),
                platforms ?? new FakePlatformPort(),
                NullLogger<OrderPricingResolver>.Instance),
            store, createLock ?? new FakeOrderCreateLock(),
            completer, NullLogger<OrderCreator>.Instance);
    }

    [Fact]
    public async Task 客户端伪造的单价被纠正为权威售价()
    {
        var coupons = new FakeCouponPort { Discount = 0m, CouponId = 0 };
        var points = new FakePointPort();
        var inventory = new FakeInventoryPort();
        var store = new FakeOrderStore();

        // 请求报 0.01，商品真实售价 25.50。
        var req = Request(couponId: 0, lineCount: 1);
        req = req with
        {
            Lines = [new OrderLineRequest(
                SpuId: 100, SkuId: 1000, Quantity: 2, UnitPrice: 0.01m,
                ProductName: "伪造价格", SkuSpecText: "红色 / M", DeliveryType: 1)]
        };

        var result = await Build(coupons, points, inventory, store,
            products: new FakeProductPort { AuthoritativePrice = 25.50m }).CreateAsync(req);

        Assert.True(result.Succeeded);

        // 关键断言：金额按 25.50 算（2 件 = 51.00），而不是按客户端报的 0.01（= 0.02）。
        // 只要这里出现 0.02，就说明金额链路仍然由客户端说了算 —— 那是个能被直接利用的漏洞。
        var order = store.Saved!;
        Assert.Equal(51.00m, order.GoodsTotal);
        Assert.Equal(51.00m, order.PayableAmount);
        var item = Assert.Single(store.SavedItems);
        Assert.Equal(25.50m, item.Price);
    }

    [Fact]
    public async Task 商品服务回查不到SKU时拒单且不落库()
    {
        var store = new FakeOrderStore();
        var result = await Build(
            new FakeCouponPort(), new FakePointPort(), new FakeInventoryPort(), store,
            products: new FakeProductPort { ReturnEmpty = true }).CreateAsync(Request(couponId: 0));

        Assert.False(result.Succeeded);
        Assert.Null(store.Saved);
        Assert.Empty(store.SavedItems);
        Assert.Empty(store.SavedOrders);
    }

    [Fact]
    public async Task 运费按平台配置收取而不是客户端报的值()
    {
        var store = new FakeOrderStore();

        // 客户端报运费 0（前端至今硬编码 0），平台配置运费 10。
        var req = Request(couponId: 0, lineCount: 1) with
        {
            Freight = new FreightRule(0m, 0m),
        };

        var result = await Build(
            new FakeCouponPort(), new FakePointPort(), new FakeInventoryPort(), store,
            platforms: new FakePlatformPort { ShippingFee = 10m }).CreateAsync(req);

        Assert.True(result.Succeeded);

        // 2 件 × 25.50 = 51.00，运费 10 → 实付 61.00。
        // 采信客户端的 0 就会得到 51.00 —— 那正是「平台运费永远收不到」的现状。
        Assert.Equal(10m, store.Saved!.Freight);
        Assert.Equal(61.00m, store.Saved.PayableAmount);
    }

    [Fact]
    public async Task 商品实付达到包邮门槛时免运费()
    {
        var store = new FakeOrderStore();
        var req = Request(couponId: 0, lineCount: 1);   // 2 件 = 51.00

        var result = await Build(
            new FakeCouponPort(), new FakePointPort(), new FakeInventoryPort(), store,
            platforms: new FakePlatformPort { ShippingFee = 10m, FreeShippingThreshold = 50m })
            .CreateAsync(req);

        Assert.True(result.Succeeded);
        Assert.Equal(0m, store.Saved!.Freight);
        Assert.Equal(51.00m, store.Saved.PayableAmount);
    }

    [Theory]
    [InlineData(DeliveryTypes.Virtual)]
    [InlineData(DeliveryTypes.SelfPickup)]
    public async Task 虚拟与自提不收运费且不为此多打一次跨服务调用(int deliveryType)
    {
        var store = new FakeOrderStore();
        var platforms = new FakePlatformPort { ShippingFee = 10m };
        var req = Request(couponId: 0, lineCount: 1);

        var result = await Build(
            new FakeCouponPort(), new FakePointPort(), new FakeInventoryPort(), store,
            products: new FakeProductPort { DeliveryType = deliveryType },
            platforms: platforms).CreateAsync(req);

        Assert.True(result.Succeeded);

        // 运费恒为 0：BUSINESS.md 6.2「只对实物快递收运费；虚拟商品与自提恒为 0」。
        Assert.Equal(0m, store.Saved!.Freight);

        // 不该为用不上的配置多打一次跨服务调用，
        // 否则商户平台服务一抖，虚拟商品与自提单会一起下不了。
        Assert.Equal(0, platforms.QueryCount);
    }

    [Fact]
    public async Task 客户端把自提商品报成快递也收不到运费()
    {
        var store = new FakeOrderStore();
        var platforms = new FakePlatformPort { ShippingFee = 10m };

        // 客户端把自提商品谎报成实物快递，并顺手报上 0 元运费。
        var req = Request(couponId: 0, lineCount: 1) with
        {
            Freight = new FreightRule(0m, 0m),
            Lines = [new OrderLineRequest(
                SpuId: 100, SkuId: 1000, Quantity: 2, UnitPrice: 25.50m,
                ProductName: "自提商品", SkuSpecText: "红色 / M",
                DeliveryType: DeliveryTypes.Express)],
        };

        var result = await Build(
            new FakeCouponPort(), new FakePointPort(), new FakeInventoryPort(), store,
            products: new FakeProductPort { DeliveryType = DeliveryTypes.SelfPickup },
            platforms: platforms).CreateAsync(req);

        Assert.True(result.Succeeded);

        // 商品服务说是自提，运费就是 0。采信客户端报的方式就能凭空多收一笔。
        Assert.Equal(0m, store.Saved!.Freight);
        Assert.Equal(51.00m, store.Saved.PayableAmount);
        Assert.Equal(0, platforms.QueryCount);
    }

    [Fact]
    public async Task 抵扣积分被夹到商品实付的100_不会白送积分()
    {
        var points = new FakePointPort();
        var store = new FakeOrderStore();

        // 商品实付 102.00 元 → 最多抵 10200 分。客户要 99999 分（余额充足），
        // 只能抵 10200，实付 0。
        var result = await Build(
            new FakeCouponPort { CouponId = 0, Discount = 0m }, points,
            new FakeInventoryPort(), store)
            .CreateAsync(Request(couponId: 0, points: 99999));

        Assert.True(result.Succeeded);
        Assert.Equal(10200L, points.LastLockedPoints);

        // 🔴 这条是重点：BUSINESS.md 8.2 规定抵扣上限是应付商品金额的 100%。
        // 不夹住的话会锁 99999 分、扣 999.99 元，而实付同样被截到 0 ——
        // 多出来的 897.99 元积分**永久消失**，一分钱也没多省。
        Assert.Equal(0m, store.Saved!.PayableAmount);
        Assert.Equal(102.00m, store.Saved.PointsDeduction);
    }

    [Fact]
    public async Task 订单归属平台按商品算而不是客户端传的0()
    {
        var store = new FakeOrderStore();

        // 客户令牌里没有 platform_id，小程序只能硬编码 0。
        // 订单服务若照单全收，平台运费就永远按「0 元平台」去查 ——
        // 后台把运费配成 10 元，顾客照样免运费，而且订单的归属平台全是 0。
        var req = Request(couponId: 0, lineCount: 1) with { PlatformId = 0 };

        var result = await Build(
            new FakeCouponPort { CouponId = 0, Discount = 0m }, new FakePointPort(),
            new FakeInventoryPort(), store,
            products: new FakeProductPort { PlatformId = 8888 },
            platforms: new FakePlatformPort { ShippingFee = 10m }).CreateAsync(req);

        Assert.True(result.Succeeded);
        Assert.Equal(8888L, store.Saved!.PlatformId);
        Assert.Equal(10m, store.Saved.Freight);
        Assert.Equal(61.00m, store.Saved.PayableAmount);
    }

    [Fact]
    public async Task 购物车里混了不同平台的商品则拒单()
    {
        var store = new FakeOrderStore();

        // 跨平台凑一单：运费按哪个平台算都不对，结算与对账也说不清。
        // 必须在落库前拒掉 —— 落库之后再发现就只能靠人工拆单了。
        var req = Request(couponId: 0, lineCount: 2);
        var productPort = new PerSkuOwnerProductPort(id => id == 1000L ? (100L, 0L) : (200L, 0L));

        var result = await Build(
            new FakeCouponPort(), new FakePointPort(), new FakeInventoryPort(), store,
            products: productPort).CreateAsync(req);

        Assert.False(result.Succeeded);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task 订单归属商户按商品算而不是客户端报的0()
    {
        var store = new FakeOrderStore();

        // 客户端报 merchantId = 0 就会被记成「平台自营」——
        // 商户结算少了一笔营业额，而订单列表看上去毫无异常。
        var req = Request(couponId: 0, lineCount: 1) with { MerchantId = 0 };

        var result = await Build(
            new FakeCouponPort { CouponId = 0, Discount = 0m }, new FakePointPort(),
            new FakeInventoryPort(), store,
            products: new FakeProductPort { MerchantId = 777 }).CreateAsync(req);

        Assert.True(result.Succeeded);
        Assert.Equal(777L, store.Saved!.MerchantId);
    }

    [Fact]
    public async Task 购物车里混了不同店铺的商品则拒单()
    {
        var store = new FakeOrderStore();
        var req = Request(couponId: 0, lineCount: 2);

        // 跨商户凑一单：结算要拆、售后要找谁发货，全说不清。
        var productPort = new PerSkuOwnerProductPort(id => id == 1000L ? (100L, 11L) : (100L, 22L));

        var result = await Build(
            new FakeCouponPort(), new FakePointPort(), new FakeInventoryPort(), store,
            products: productPort).CreateAsync(req);

        Assert.False(result.Succeeded);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task 客户端把spuId填错会被纠正_否则指定商品的券能用错商品上()
    {
        var coupons = new FakeCouponPort { CouponId = 77, Discount = 5m };
        var store = new FakeOrderStore();

        // 请求里报 spuId = 999（某个能享受定向券的商品），实际买的 SKU 属于 spuId 100。
        // 券与活动都支持「指定商品」，不纠正的话「仅限 999」的券就能用在 100 上；
        // 订单行也会照抄 999，于是评价（SPU 级）与报表全记到错的商品上。
        var req = Request(couponId: 77, lineCount: 1) with
        {
            Lines = [new OrderLineRequest(
                SpuId: 999, SkuId: 1000, Quantity: 2, UnitPrice: 25.50m,
                ProductName: "商品0", SkuSpecText: "红色 / M", DeliveryType: 1)]
        };

        var result = await Build(
            coupons, new FakePointPort(), new FakeInventoryPort(), store,
            products: new FakeProductPort { ProductId = 100 }).CreateAsync(req);

        Assert.True(result.Succeeded);

        // 落库的订单行记的是真实 SPU
        Assert.Equal(100L, store.SavedItems[0].SpuId);

        // 传给券/活动的也是真实 SPU —— 券按 999 定向的话这里就该是 0 优惠
        Assert.Equal(100L, coupons.LastLines[0].SpuId);
    }

    /// <summary>按 SKU 分别返回不同平台 / 商户的端口替身，用来构造跨平台或跨店铺的购物车。</summary>
    private sealed class PerSkuOwnerProductPort : IProductPort
    {
        private readonly Func<long, (long PlatformId, long MerchantId)> _ownerOf;

        public PerSkuOwnerProductPort(Func<long, (long PlatformId, long MerchantId)> ownerOf)
            => _ownerOf = ownerOf;

        public Task<IReadOnlyDictionary<long, SkuPriceInfo>> GetSkuPricesAsync(
            IReadOnlyCollection<long> skuIds, CancellationToken ct = default)
        {
            var result = new Dictionary<long, SkuPriceInfo>();
            foreach (var id in skuIds)
            {
                var (platformId, merchantId) = _ownerOf(id);
                result[id] = new SkuPriceInfo(
                    id, 100L, 25.50m, Enabled: true, SpuApproved: true, SpuOnShelf: true,
                    merchantId, platformId, DeliveryTypes.Express);
            }

            return Task.FromResult<IReadOnlyDictionary<long, SkuPriceInfo>>(result);
        }
    }

    [Fact]
    public async Task 支付已关闭的订单被拒_而不是当成重复回调放行()
    {
        var store = new FakeOrderStore();
        var inventory = new FakeInventoryPort();
        var completer = new OrderPaymentCompleter(
            store, inventory, new FakePointPort(), new FakeCouponPort(),
            NullLogger<OrderPaymentCompleter>.Instance);

        var order = new Order
        {
            OrderNo = "CLOSED-1",
            CustomerId = CustomerId,
            PayableAmount = 51m,

            // 超时关单之后的状态：券 / 积分 / 库存都已经被释放回去了
            Status = OrderStatuses.Cancelled,
        };
        store.Saved = order;
        store.SavedItems.Add(new OrderItem
        {
            OrderNo = order.OrderNo, SkuId = 1000, Quantity = 2, Price = 25.50m,
        });

        var outcome = await completer.CompleteAsync(order);

        // 🔴 这里以前返回「已处理完成」：关单与支付回调的竞态下，
        // 用户的钱扣了、单却已作废，而系统报的是**成功** ——
        // 没有任何东西会触发对账，只能等用户来投诉。
        // 拒掉之后上游才能把这笔钱原路退回。
        Assert.False(outcome.Succeeded);
        Assert.Equal(0, inventory.DeductCount);
    }

    [Fact]
    public async Task 重复支付已付款的订单仍按幂等放行()
    {
        // 上一条的反面：真正的重复回调（网关重试、消息重投）**不能**被拒，
        // 否则用户付了两次钱、只发货一次。幂等与拒付要分开判。
        var store = new FakeOrderStore();
        var inventory = new FakeInventoryPort();
        var completer = new OrderPaymentCompleter(
            store, inventory, new FakePointPort(), new FakeCouponPort(),
            NullLogger<OrderPaymentCompleter>.Instance);

        var order = new Order
        {
            OrderNo = "PAID-1",
            CustomerId = CustomerId,
            PayableAmount = 51m,
            Status = OrderStatuses.PendingShipment,
        };
        store.Saved = order;
        store.SavedItems.Add(new OrderItem
        {
            OrderNo = order.OrderNo, SkuId = 1000, Quantity = 2, Price = 25.50m,
        });

        var outcome = await completer.CompleteAsync(order);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.AlreadyCompleted);

        // 关键：绝不能再扣一次库存
        Assert.Equal(0, inventory.DeductCount);
    }
    [Fact]
    public async Task 正常下单四步都执行并落单()
    {
        var coupons = new FakeCouponPort { Discount = 5m, CouponId = 77 };
        var points = new FakePointPort();
        var inventory = new FakeInventoryPort();
        var store = new FakeOrderStore();

        var result = await Build(coupons, points, inventory, store).CreateAsync(Request(points: 100));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.FailedStep);
        Assert.Equal(1, coupons.OccupyCount);
        Assert.Equal(1, points.LockCount);
        Assert.Equal(2, inventory.LockCount);        // 两个 SKU 各锁一次
        Assert.NotNull(store.Saved);
    }

    [Fact]
    public async Task 幂等重复下单直接返回首次订单且不重复占用()
    {
        var coupons = new FakeCouponPort { Discount = 5m, CouponId = 77 };
        var points = new FakePointPort();
        var inventory = new FakeInventoryPort();
        var store = new FakeOrderStore();

        var creator = Build(coupons, points, inventory, store);
        var first = await creator.CreateAsync(Request());
        var second = await creator.CreateAsync(Request());

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.True(second.AlreadyCreated);
        Assert.Equal(first.OrderNo, second.OrderNo);

        // 关键：重复请求不能再占一次券 / 锁一次库存。
        // 第一单锁了 2 个 SKU；第二单若再执行一遍，占用会翻倍——库存被双倍冻结。
        Assert.Equal(1, coupons.OccupyCount);
        Assert.Equal(2, inventory.LockCount);
        Assert.Single(store.SavedOrders);
    }

    [Fact]
    public async Task 步骤一占券失败时不做任何回滚且不落单()
    {
        var coupons = new FakeCouponPort { ThrowOnOccupy = true };
        var points = new FakePointPort();
        var inventory = new FakeInventoryPort();
        var store = new FakeOrderStore();

        var result = await Build(coupons, points, inventory, store).CreateAsync(Request(points: 100));

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.FailedStep);
        // ① 是第一步，前面没有东西要退；后面的步骤也不该被执行
        Assert.Equal(0, points.LockCount);
        Assert.Equal(0, inventory.LockCount);
        Assert.Equal(0, coupons.ReleaseCount);
        Assert.Empty(store.SavedOrders);
    }

    [Fact]
    public async Task 步骤二锁积分失败时回滚步骤一且不执行步骤三()
    {
        var coupons = new FakeCouponPort { Discount = 5m, CouponId = 77 };
        var points = new FakePointPort { ReturnFalseOnLock = true };
        var inventory = new FakeInventoryPort();
        var store = new FakeOrderStore();

        var result = await Build(coupons, points, inventory, store).CreateAsync(Request(points: 100));

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.FailedStep);
        Assert.Equal(1, coupons.ReleaseCount);      // 步骤一的券占用已退
        Assert.Equal(0, inventory.LockCount);      // 步骤三根本没跑
        Assert.Empty(store.SavedOrders);
    }

    [Fact]
    public async Task 步骤三锁库存失败时把已锁的SKU也释放并回滚步骤二和一步()
    {
        var coupons = new FakeCouponPort { Discount = 5m, CouponId = 77 };
        var points = new FakePointPort();
        // 第 2 个 SKU 返回 false：模拟「第一个锁成功、第二个库存不足」
        var inventory = new FakeInventoryPort { FailOnSkuId = 1001 };
        var store = new FakeOrderStore();

        var result = await Build(coupons, points, inventory, store).CreateAsync(Request(points: 100));

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.FailedStep);

        // 最容易漏的一处：第 1 个 SKU 已经锁上了，必须释放
        Assert.Contains(1000, inventory.ReleasedSkus);
        Assert.DoesNotContain(1001, inventory.ReleasedSkus);   // 它从没锁成功，不用退

        Assert.Equal(1, points.UnfreezeCount);
        Assert.Equal(1, coupons.ReleaseCount);
        Assert.Empty(store.SavedOrders);
    }

    [Fact]
    public async Task 步骤四落单失败时逆序回滚库存积分券()
    {
        var coupons = new FakeCouponPort { Discount = 5m, CouponId = 77 };
        var points = new FakePointPort();
        var inventory = new FakeInventoryPort();
        var store = new FakeOrderStore { ThrowOnSave = true };

        var result = await Build(coupons, points, inventory, store).CreateAsync(Request(points: 100));

        Assert.False(result.Succeeded);
        Assert.Equal(4, result.FailedStep);

        // 逆序回滚的每一项都要发生
        Assert.Equal(2, inventory.ReleaseCount);      // 两个 SKU 都释放
        Assert.Equal(1, points.UnfreezeCount);
        Assert.Equal(1, coupons.ReleaseCount);
        Assert.Empty(store.SavedOrders);
    }

    [Fact]
    public async Task 回滚本身失败时不影响返回原始错误()
    {
        var coupons = new FakeCouponPort { Discount = 5m, CouponId = 77, ThrowOnRelease = true };
        var points = new FakePointPort();
        var inventory = new FakeInventoryPort();
        var store = new FakeOrderStore { ThrowOnSave = true };

        var result = await Build(coupons, points, inventory, store).CreateAsync(Request(points: 100));

        // 回滚失败绝不能把「落单失败」这个原始错误覆盖掉——
        // 原始错误才是排查的起点，回滚失败另记日志
        Assert.False(result.Succeeded);
        Assert.Equal(4, result.FailedStep);
        Assert.Contains("创建订单失败", result.Error);
    }

    [Fact]
    public async Task 实付为0的订单直接跳到待发货()
    {
        var coupons = new FakeCouponPort();
        var points = new FakePointPort();
        var inventory = new FakeInventoryPort();
        var store = new FakeOrderStore();

        // 2 个 SKU × 25.50 × 2 = 102.00，用 10200 积分全部抵扣掉
        var result = await Build(coupons, points, inventory, store)
            .CreateAsync(Request(points: 10200));

        Assert.True(result.Succeeded);
        Assert.Equal(0m, store.Saved!.PayableAmount);
        Assert.Equal(OrderStatuses.PendingShipment, store.Saved.Status);
    }

    [Fact]
    public async Task 没用券也没用积分时不调用积分服务()
    {
        var coupons = new FakeCouponPort();
        var points = new FakePointPort();
        var inventory = new FakeInventoryPort();
        var store = new FakeOrderStore();

        var result = await Build(coupons, points, inventory, store).CreateAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal(0, points.LockCount);        // 没有积分抵扣就不该调积分服务
    }

    [Fact]
    public async Task 空订单行被拒()
    {
        var request = Request() with { Lines = Array.Empty<OrderLineRequest>() };
        var result = await Build(new FakeCouponPort(), new FakePointPort(), new FakeInventoryPort(), new FakeOrderStore())
            .CreateAsync(request);

        Assert.False(result.Succeeded);
        Assert.Contains("没有任何商品行", result.Error);
    }

    [Fact]
    public async Task 占券时带上SPU与行金额供券判定适用范围()
    {
        var coupons = new FakeCouponPort { Discount = 5m, CouponId = 77 };
        var inventory = new FakeInventoryPort();
        var store = new FakeOrderStore();

        await Build(coupons, new FakePointPort(), inventory, store).CreateAsync(Request());

        // 少传 SpuId 或金额算错，营销侧就判不出这张券能不能用于这些商品——
        // 结果是「有券但没优惠」，用户看到的现象就是券没用上，而且很难查。
        Assert.Equal(2, coupons.LastLines.Count);
        Assert.Equal(100, coupons.LastLines[0].SpuId);
        Assert.Equal(1000, coupons.LastLines[0].SkuId);
        Assert.Equal(51.00m, coupons.LastLines[0].Amount);   // 25.50 × 2
    }

    [Fact]
    public async Task 拿不到客户锁时直接拒绝且不占用任何资源()
    {
        var coupons = new FakeCouponPort { Discount = 5m, CouponId = 77 };
        var points = new FakePointPort();
        var inventory = new FakeInventoryPort();
        var store = new FakeOrderStore();
        var createLock = new FakeOrderCreateLock { AcquireSucceeds = false };

        var result = await Build(coupons, points, inventory, store, createLock)
            .CreateAsync(Request(points: 100));

        // 拿不到锁说明另一个下单正在跑。这时**一个资源都不能占**——
        // 否则两个请求各占一份，幂等键唯一索引只能保证一张单落库，拦不住多占的副作用。
        Assert.False(result.Succeeded);
        Assert.Equal(1, createLock.AcquireCount);
        Assert.Equal(0, coupons.OccupyCount);
        Assert.Equal(0, points.LockCount);
        Assert.Equal(0, inventory.LockCount);
        Assert.Empty(store.SavedOrders);
        Assert.Contains("正在提交中", result.Error);
    }

    [Fact]
    public async Task 下单结束时客户锁一定被释放()
    {
        var createLock = new FakeOrderCreateLock();

        await Build(new FakeCouponPort(), new FakePointPort(), new FakeInventoryPort(), new FakeOrderStore(), createLock)
            .CreateAsync(Request());

        Assert.Equal(1, createLock.ReleaseCount);

        // 失败路径也必须释放，否则这个客户之后所有下单都会被卡住
        await Build(new FakeCouponPort(), new FakePointPort(), new FakeInventoryPort(),
                new FakeOrderStore { ThrowOnSave = true }, createLock)
            .CreateAsync(Request(key: "idem-2"));

        Assert.Equal(2, createLock.ReleaseCount);
    }

    [Fact]
    public async Task 并发下已有同一张单时回退本次占用并返回已存在的订单()
    {
        var coupons = new FakeCouponPort { Discount = 5m, CouponId = 77 };
        var points = new FakePointPort();
        var inventory = new FakeInventoryPort();
        var store = new FakeOrderStore();
        var existing = new Order
        {
            Id = 111, OrderNo = "20260101000000123456", CustomerId = CustomerId,
            IdempotencyKey = "idem-1", Status = OrderStatuses.PendingPayment,
            GoodsTotal = 50m, PayableAmount = 50m
        };
        store.ExistingOnSave = existing;

        var result = await Build(coupons, points, inventory, store).CreateAsync(Request(points: 100));

        // 落库的是先到的那张单，返回的必须是它的订单号，不能是本次生成的废号
        Assert.True(result.Succeeded);
        Assert.True(result.AlreadyCreated);
        Assert.Equal("20260101000000123456", result.OrderNo);

        // 本次占的券 / 积分 / 库存都必须退回去，否则先到那张单的额度被白白吃掉
        Assert.Equal(2, inventory.ReleaseCount);
        Assert.Equal(1, points.UnfreezeCount);
        Assert.Equal(1, coupons.ReleaseCount);
    }
    [Fact]
    public async Task 券Id为0时完全不调用占券因为选不选券是客户端的事()
    {
        var coupons = new FakeCouponPort { Discount = 5m, CouponId = 77 };
        var store = new FakeOrderStore();

        var result = await Build(coupons, new FakePointPort(), new FakeInventoryPort(), store)
            .CreateAsync(Request(couponId: 0));

        // 结算页已经把「最优券」算好交给客户端了，用户点「不使用券」时传 0。
        // 服务端要是再自动挑一张，到手价就跟页面上显示的不一致——用户投诉的是这个。
        Assert.True(result.Succeeded);
        Assert.Equal(0, coupons.OccupyCount);
        Assert.Equal(0, store.Saved!.CouponId);
        Assert.Equal(0m, store.Saved.CouponDiscount);
    }

    [Fact]
    public async Task 指定了券却没占到时按没券继续下单而不是整单失败()
    {
        // 券可能在这几秒里被别人领走或过期。这属于正常业务，不该把整单判失败。
        var coupons = new FakeCouponPort { CouponId = 0, Discount = 0m };
        var store = new FakeOrderStore();

        var result = await Build(coupons, new FakePointPort(), new FakeInventoryPort(), store)
            .CreateAsync(Request(couponId: 77));

        Assert.True(result.Succeeded);
        Assert.Equal(1, coupons.OccupyCount);
        Assert.Equal(0, coupons.ReleaseCount);      // 本来就没占上，不必退
        Assert.Equal(0, store.Saved!.CouponId);
    }
    [Fact]
    public async Task 实付0元的单在下单当场就结清占用而不是留着等支付()
    {
        // 0 元单没有支付这一步，也就没有任何人会来跑支付收尾。
        // 不当场结清的话：库存一直锁着、积分一直冻着、券一直占着，
        // 而用户在订单列表里看到的是「待发货」——看起来一切正常，实际全是悬空占用。
        var coupons = new FakeCouponPort { Discount = 5m, CouponId = 77 };
        var points = new FakePointPort();
        var inventory = new FakeInventoryPort();
        var store = new FakeOrderStore();

        // 默认 2 个 SKU × 25.50 × 2 件 = 102.00；券减 5.00 → 97.00；
        // 9700 积分抵 97.00 → 实付 0.00
        var result = await Build(coupons, points, inventory, store)
            .CreateAsync(Request(couponId: 77, points: 9700));

        Assert.True(result.Succeeded);
        Assert.Equal(OrderStatuses.PendingShipment, store.Saved!.Status);

        // 冻结 → 实扣、占用 → 核销、锁定 → 扣减，三样都要发生
        Assert.Equal(1, points.ConsumeCount);
        Assert.Equal(0, points.Frozen);
        Assert.Equal(1, coupons.ConsumeCount);
        Assert.Equal(2, inventory.DeductCount);
        Assert.Equal(0, inventory.ReleaseCount);
    }
    // ---------------- 假端口 ----------------

    private sealed class FakeOrderCreateLock : IOrderCreateLock
    {
        public bool AcquireSucceeds = true;
        public int AcquireCount;
        public int ReleaseCount;

        public Task<IOrderCreateLockHandle?> TryAcquireAsync(
            long customerId, TimeSpan waitFor, TimeSpan ttl, CancellationToken ct = default)
        {
            AcquireCount++;
            if (!AcquireSucceeds) return Task.FromResult<IOrderCreateLockHandle?>(null);
            return Task.FromResult<IOrderCreateLockHandle?>(new FakeHandle(this));
        }

        private sealed class FakeHandle : IOrderCreateLockHandle
        {
            private readonly FakeOrderCreateLock _owner;
            public FakeHandle(FakeOrderCreateLock owner) => _owner = owner;
            public ValueTask DisposeAsync()
            {
                _owner.ReleaseCount++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeCouponPort : ICouponPort
    {
        public int OccupyCount;
        public int ReleaseCount;
        public int ConsumeCount;
        public long CouponId;
        public decimal Discount;
        public bool ThrowOnOccupy;
        public bool ThrowOnRelease;
        public List<CouponPortLine> LastLines = new();

        public Task<(long CouponId, decimal Discount)> OccupyAsync(
            long customerId, string orderNo, long couponId,
            IReadOnlyList<CouponPortLine> lines, CancellationToken ct = default)
        {
            OccupyCount++;
            LastLines = lines.ToList();
            if (ThrowOnOccupy) throw new InvalidOperationException("营销服务不可用");
            return Task.FromResult((CouponId, Discount));
        }

        public Task ReleaseAsync(long customerId, string orderNo, CancellationToken ct = default)
        {
            ReleaseCount++;
            if (ThrowOnRelease) throw new InvalidOperationException("回退券失败");
            return Task.CompletedTask;
        }

        public Task ConsumeAsync(long customerId, string orderNo, CancellationToken ct = default)
        {
            ConsumeCount++;
            return Task.CompletedTask;
        }

        /// <summary>可用的券试算结果，默认只有一张「默认券」。</summary>
        public List<CouponQuoteOption> QuoteOptions { get; } =
            [new CouponQuoteOption(77, "满减", 5m, "2026-12-31", true)];

        /// <summary>被试算的次数。结算试算是只读的，占用次数不该因此增加。</summary>
        public int QuoteCount { get; private set; }

        public Task<IReadOnlyList<CouponQuoteOption>> QuoteAsync(
            long customerId, IReadOnlyList<CouponPortLine> lines, CancellationToken ct = default)
        {
            QuoteCount++;
            return Task.FromResult<IReadOnlyList<CouponQuoteOption>>(QuoteOptions);
        }
    }

    /// <summary>商品定价端口的替身。</summary>
    /// <remarks>
    /// 默认「原样回显请求里的价格」：既有用例传的就是正确售价，
    /// 回显能让它们照常通过；而要验证「客户端报的价格会不会被纠正」时，
    /// 把 <see cref="AuthoritativePrice"/> 设成别的值即可。
    /// </remarks>
    private sealed class FakeProductPort : IProductPort
    {
        /// <summary>
        /// 权威售价。默认 25.50 —— 与本文件里下单用例用的价格一致，
        /// 所以「客户端报 25.50、商品也是 25.50」的正常路径仍然算出同样的金额。
        /// </summary>
        public decimal? AuthoritativePrice { get; set; }

        /// <summary>让回查返回「查不到」，用来验证服务不可用时是否拒单。</summary>
        public bool ReturnEmpty { get; set; }

        /// <summary>权威配送方式，默认实物快递。用于验证运费只对快递收取。</summary>
        public int DeliveryType { get; set; } = DeliveryTypes.Express;

        /// <summary>商品归属平台。订单归属与运费都按它算，不采信客户端传的 platformId。</summary>
        public long PlatformId { get; set; }

        /// <summary>商品归属商户。0 表示平台自营。</summary>
        public long MerchantId { get; set; }

        /// <summary>权威商品名。为空时订单行沿用请求里的名称（老用例不做名称断言）。</summary>
        public string SkuName { get; set; } = string.Empty;

        /// <summary>权威规格文本。</summary>
        public string SkuSpecText { get; set; } = string.Empty;

        /// <summary>
        /// SKU 真正所属的 SPU Id。默认与订单行里报的 SpuId 一致；
        /// 要验证「客户端把 spuId 填错」时单独设成别的值。
        /// </summary>
        public long ProductId { get; set; } = 100L;

        /// <summary>被回查过的 SKU 集合。</summary>
        public List<long> Queried { get; } = [];

        /// <inheritdoc />
        public Task<IReadOnlyDictionary<long, SkuPriceInfo>> GetSkuPricesAsync(
            IReadOnlyCollection<long> skuIds, CancellationToken ct = default)
        {
            Queried.AddRange(skuIds);

            if (ReturnEmpty) return Task.FromResult<IReadOnlyDictionary<long, SkuPriceInfo>>(
                new Dictionary<long, SkuPriceInfo>());

            var result = new Dictionary<long, SkuPriceInfo>();
            foreach (var id in skuIds)
            {
                result[id] = new SkuPriceInfo(
                    id, ProductId, AuthoritativePrice ?? 25.50m,
                    Enabled: true, SpuApproved: true, SpuOnShelf: true, MerchantId,
                    PlatformId, DeliveryType, SkuName, SkuSpecText);
            }

            return Task.FromResult<IReadOnlyDictionary<long, SkuPriceInfo>>(result);
        }
    }

    /// <summary>平台运费配置端口的替身。默认运费 0，与「平台自营未配运费」一致。</summary>
    private sealed class FakePlatformPort : IPlatformPort
    {
        /// <summary>平台运费。</summary>
        public decimal ShippingFee { get; set; }

        /// <summary>满额包邮门槛，0 表示不启用。</summary>
        public decimal FreeShippingThreshold { get; set; }

        /// <summary>被查询的次数，用来断言「虚拟/自提单不该为此多打一次跨服务调用」。</summary>
        public int QueryCount { get; private set; }

        /// <inheritdoc />
        public Task<ShippingConfig> GetShippingConfigAsync(long platformId, CancellationToken ct = default)
        {
            QueryCount++;
            return Task.FromResult(new ShippingConfig(ShippingFee, FreeShippingThreshold));
        }
    }

    /// <summary>活动优惠试算端口的替身。默认「没有活动优惠」，用例需要时自行赋值。</summary>
    private sealed class FakeActivityPort : IActivityPort
    {
        /// <summary>逐 SKU 的活动优惠，供用例构造「有满减」场景。</summary>
        public Dictionary<long, decimal> BySku { get; } = [];

        /// <summary>报价被调用的次数，用来断言下单确实去算了。</summary>
        public int CallCount { get; private set; }

        /// <summary>被传入的已选券 Id，用来验证活动与券互斥时传对了。</summary>
        public long LastCouponId { get; private set; }

        /// <inheritdoc />
        public Task<IReadOnlyList<(long SkuId, decimal ActivityDiscount)>> QuoteAsync(
            long customerId, long platformId, long sessionId, long couponId,
            IReadOnlyList<(long SpuId, long SkuId, decimal Amount)> lines,
            CancellationToken ct = default)
        {
            CallCount++;
            LastCouponId = couponId;

            return Task.FromResult<IReadOnlyList<(long, decimal)>>(
                lines.Where(a => BySku.TryGetValue(a.SkuId, out var d) && d != 0m)
                    .Select(a => (a.SkuId, BySku[a.SkuId]))
                    .ToList());
        }
    }

    private sealed class FakePointPort : IPointPort
    {
        public int LockCount;
        public int UnfreezeCount;
        public int ConsumeCount;
        public int RecoverCount;
        public bool ReturnFalseOnLock;

        /// <summary>最近一次退款回收用的比例，供用例断言「按本次退款额算比例」。</summary>
        public decimal LastRecoverRatio;

        /// <summary>
        /// 最近一次<b>实际</b>锁定的积分数。断言它而不是断言请求值：
        /// 「夹到上限」这件事发生在编排器里，端口只看到夹完之后的数字。
        /// </summary>
        public long LastLockedPoints;

        /// <summary>还冻着的积分数。实扣后必须归零，否则就是一笔悬空占用。</summary>
        public long Frozen;

        public Task<bool> LockAsync(long customerId, string orderNo, long points, CancellationToken ct = default)
        {
            LockCount++;
            LastLockedPoints = points;
            Frozen += points;
            return Task.FromResult(!ReturnFalseOnLock);
        }

        public Task UnfreezeAsync(long customerId, string orderNo, CancellationToken ct = default)
        {
            UnfreezeCount++;
            Frozen = 0;
            return Task.CompletedTask;
        }

        public Task ConsumeAsync(long customerId, string orderNo, CancellationToken ct = default)
        {
            ConsumeCount++;
            Frozen = 0;
            return Task.CompletedTask;
        }

        /// <summary>退款按比例回收积分。记下比例供断言，行为本身在积分服务里。</summary>
        /// <param name="customerId">客户 Id。</param>
        /// <param name="orderNo">订单号。</param>
        /// <param name="refundRatio">退款比例。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>异步任务。</returns>
        public Task RecoverByRefundAsync(
            long customerId, string orderNo, decimal refundRatio, CancellationToken ct = default)
        {
            RecoverCount++;
            LastRecoverRatio = refundRatio;
            return Task.CompletedTask;
        }

        /// <summary>最近一次按订单发放的实付金额。没调用过就是 -1。</summary>
        public decimal LastEarnedAmount = -1m;

        public Task EarnByOrderAsync(
            long customerId, string orderNo, decimal paidAmount, CancellationToken ct = default)
        {
            LastEarnedAmount = paidAmount;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeInventoryPort : IInventoryPort
    {
        public int LockCount;
        public int ReleaseCount;
        public int DeductCount;
        public int ReplenishCount;
        public long FailOnSkuId;
        public List<long> ReleasedSkus = new();

        public Task<bool> LockAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default)
        {
            if (skuId == FailOnSkuId) return Task.FromResult(false);
            LockCount++;
            return Task.FromResult(true);
        }

        public Task ReleaseAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default)
        {
            ReleaseCount++;
            ReleasedSkus.Add(skuId);
            return Task.CompletedTask;
        }

        public Task DeductAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default)
        {
            DeductCount++;
            return Task.CompletedTask;
        }

        public Task ReplenishAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default)
        {
            ReplenishCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOrderStore : IOrderStore
    {
        public Order? Saved;
        public List<Order> SavedOrders = new();
        public bool ThrowOnSave;

        /// <summary>模拟「并发下已有同一张单」：非空时 SaveAsync 直接返回它。</summary>
        public Order? ExistingOnSave;

        public Task<Order?> FindByIdempotencyKeyAsync(
            long customerId, string idempotencyKey, CancellationToken ct = default)
            => Task.FromResult(SavedOrders.FirstOrDefault(
                a => a.CustomerId == customerId && a.IdempotencyKey == idempotencyKey));

        /// <summary>已保存的订单行。按订单 Id 存，模拟真实的订单行表。</summary>
        public List<OrderItem> SavedItems = new();

        public Task<Order?> FindByOrderNoAsync(string orderNo, CancellationToken ct = default)
            => Task.FromResult(SavedOrders.FirstOrDefault(a => a.OrderNo == orderNo));

        // 必须真的返回订单行：支付收尾要靠它逐个 SKU 扣减库存，
        // 这里返回空集合的话「扣没扣库存」这条断言永远测不出问题。
        public Task<List<Order>> FindTimeoutCandidatesAsync(
            DateTime deadlineUtc, int limit, CancellationToken ct = default)
            => Task.FromResult(new List<Order>());

        public Task<List<OrderItem>> ListItemsAsync(long orderId, CancellationToken ct = default)
            => Task.FromResult(SavedItems.Where(a => a.OrderId == orderId).ToList());

        public Task<(List<Order> Orders, long Total)> ListByCustomerAsync(
            long customerId, int status, int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult((SavedOrders, (long)SavedOrders.Count));

        public Task<(List<Order> Orders, long Total)> ListAsync(
            int status, string keyword, long platformId, long merchantId,
            long customerId, string customerNo, DateTime? from, DateTime? to,
            int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult((SavedOrders, (long)SavedOrders.Count));

        public Task<Dictionary<long, OrderItemAggregate>> AggregateItemsAsync(
            IReadOnlyCollection<long> orderIds, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<long, OrderItemAggregate>());

        public Task<int> TryTransitStatusAsync(
            long orderId, int fromStatus, int toStatus, CancellationToken ct = default,
            DateTime? paidAt = null, DateTime? completedAt = null)
        {
            // 记下状态迁移时传入的时间戳，供用例断言「支付时间确实被盖上了」。
            // 之前这里直接返回 1，paid_at 写没写、写得对不对都没人能测——
            // 而报表的 GMV 全靠它，少一个时间戳就是一块金额凭空消失。
            LastPaidAt = paidAt;
            LastCompletedAt = completedAt;
            return Task.FromResult(1);
        }

        /// <summary>最后一次状态迁移传入的支付时间。</summary>
        public DateTime? LastPaidAt;

        /// <summary>最后一次状态迁移传入的完成时间。</summary>
        public DateTime? LastCompletedAt;

        /// <summary>发货：下单链路的用例不依赖它，返回 1 表示状态改成功即可。</summary>
        /// <param name="orderId">订单 Id。</param>
        /// <param name="fromStatus">期望的原状态。</param>
        /// <param name="toStatus">目标状态。</param>
        /// <param name="logisticsCompanyId">物流公司 Id。</param>
        /// <param name="logisticsCompanyName">物流公司名称。</param>
        /// <param name="trackingNo">运单号。</param>
        /// <param name="shippedAt">发货时间。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>1。</returns>
        public Task<int> TryShipAsync(
            long orderId, int fromStatus, int toStatus,
            long logisticsCompanyId, string logisticsCompanyName, string trackingNo,
            DateTime shippedAt, CancellationToken ct = default)
            => Task.FromResult(1);

        /// <summary>行级已退余额聚合：下单链路的用例不依赖它，返回空即可。</summary>
        /// <param name="orderId">订单 Id。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>空字典。</returns>
        public Task<IReadOnlyDictionary<long, RefundedItemBalance>> AggregateRefundedItemsAsync(
            long orderId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<long, RefundedItemBalance>>(
                new Dictionary<long, RefundedItemBalance>());

        /// <summary>写退款记录：下单链路的用例不依赖它，回一个固定 Id。</summary>
        /// <param name="refund">退款记录。</param>
        /// <param name="items">退款明细。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>固定 Id。</returns>
        public Task<long> SaveRefundAsync(
            OrderRefund refund, IReadOnlyCollection<OrderRefundItem> items, CancellationToken ct = default)
            => Task.FromResult(1L);

        /// <summary>累加已退金额：下单链路的用例不依赖它，返回 1 表示生效。</summary>
        /// <param name="orderId">订单 Id。</param>
        /// <param name="fromStatus">期望的原状态。</param>
        /// <param name="amount">本次退款金额。</param>
        /// <param name="fullyRefunded">是否退完。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>1。</returns>
        public Task<int> TryApplyRefundAsync(
            long orderId, int fromStatus, decimal amount, bool fullyRefunded,
            CancellationToken ct = default)
            => Task.FromResult(1);

        /// <summary>退款记录查询：下单链路的用例不依赖它，返回空列表即可。</summary>
        /// <param name="orderId">订单 Id。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>空列表。</returns>
        public Task<IReadOnlyList<(OrderRefund Refund, IReadOnlyList<OrderRefundItem> Items)>>
            ListRefundsAsync(long orderId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(OrderRefund, IReadOnlyList<OrderRefundItem>)>>([]);

        /// <summary>报表聚合：下单链路的用例不依赖它，返回空聚合即可。</summary>
        /// <param name="from">区间起。</param>
        /// <param name="to">区间止。</param>
        /// <param name="merchantId">商户 Id。</param>
        /// <param name="platformId">平台 Id。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>全零聚合。</returns>
        public Task<OrderAggregateRow> AggregateAsync(
            DateTime from, DateTime to, long merchantId, long platformId, CancellationToken ct = default)
            => Task.FromResult(new OrderAggregateRow(0, 0, 0, 0m));

        /// <summary>按 Id 取订单：下单链路的用例不依赖它，返回 null 即可。</summary>
        /// <param name="orderId">订单 Id。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>null。</returns>
        public Task<Order?> GetByIdAsync(long orderId, CancellationToken ct = default)
            => Task.FromResult<Order?>(null);

        /// <summary>成交额汇总：下单链路的用例不依赖它，返回 0 即可。</summary>
        /// <param name="orderNos">订单号集合。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>0。</returns>
        public Task<decimal> SumPayableByOrderNosAsync(
            IReadOnlyCollection<string> orderNos, CancellationToken ct = default)
            => Task.FromResult(0m);

        public Task<Order> SaveAsync(Order order, IReadOnlyCollection<OrderItem> items, CancellationToken ct = default)
        {
            if (ThrowOnSave) throw new InvalidOperationException("落单失败");

            if (ExistingOnSave is not null)
            {
                // 返回的是另一张单 → OrderCreator 必须认出「这不是我刚写的那张」并回退本次占用
                return Task.FromResult(ExistingOnSave);
            }

            order.Id = 999;
            order.CreatedAt = DateTime.UtcNow;
            Saved = order;
            SavedOrders.Add(order);

            foreach (var item in items)
            {
                item.OrderId = order.Id;
                SavedItems.Add(item);
            }

            return Task.FromResult(order);
        }
    }
}
