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
        FakeOrderCreateLock? createLock = null)
    {
        var completer = new OrderPaymentCompleter(
            store, inventory, points, coupons, NullLogger<OrderPaymentCompleter>.Instance);

        return new OrderCreator(
            coupons, points, inventory, store, createLock ?? new FakeOrderCreateLock(),
            completer, NullLogger<OrderCreator>.Instance);
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
    }

    private sealed class FakePointPort : IPointPort
    {
        public int LockCount;
        public int UnfreezeCount;
        public int ConsumeCount;
        public bool ReturnFalseOnLock;

        /// <summary>还冻着的积分数。实扣后必须归零，否则就是一笔悬空占用。</summary>
        public long Frozen;

        public Task<bool> LockAsync(long customerId, string orderNo, long points, CancellationToken ct = default)
        {
            LockCount++;
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
        public Task<List<OrderItem>> ListItemsAsync(long orderId, CancellationToken ct = default)
            => Task.FromResult(SavedItems.Where(a => a.OrderId == orderId).ToList());

        public Task<(List<Order> Orders, long Total)> ListByCustomerAsync(
            long customerId, int status, int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult((SavedOrders, (long)SavedOrders.Count));

        public Task<(List<Order> Orders, long Total)> ListAsync(
            int status, string keyword, long platformId, long merchantId,
            int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult((SavedOrders, (long)SavedOrders.Count));

        public Task<Dictionary<long, OrderItemAggregate>> AggregateItemsAsync(
            IReadOnlyCollection<long> orderIds, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<long, OrderItemAggregate>());

        public Task<int> TryTransitStatusAsync(
            long orderId, int fromStatus, int toStatus, CancellationToken ct = default)
            => Task.FromResult(1);

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