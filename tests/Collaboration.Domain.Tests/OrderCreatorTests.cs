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

    private static CreateOrderRequest Request(
        string key = "idem-1", long couponId = 0, long points = 0, int lineCount = 2)
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

    private static OrderCreator Build(
        FakeCouponPort coupons, FakePointPort points, FakeInventoryPort inventory, FakeOrderStore store)
        => new(coupons, points, inventory, store, NullLogger<OrderCreator>.Instance);

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

    // ---------------- 假端口 ----------------

    private sealed class FakeCouponPort : ICouponPort
    {
        public int OccupyCount;
        public int ReleaseCount;
        public long CouponId;
        public decimal Discount;
        public bool ThrowOnOccupy;
        public bool ThrowOnRelease;

        public async Task<(long CouponId, decimal Discount)> OccupyAsync(
            long customerId, string orderNo, long couponId,
            IReadOnlyList<OrderLineInput> lines, CancellationToken ct = default)
        {
            OccupyCount++;
            if (ThrowOnOccupy) throw new InvalidOperationException("营销服务不可用");
            return (CouponId, Discount);
        }

        public Task ReleaseAsync(long customerId, string orderNo, CancellationToken ct = default)
        {
            ReleaseCount++;
            if (ThrowOnRelease) throw new InvalidOperationException("回退券失败");
            return Task.CompletedTask;
        }
    }

    private sealed class FakePointPort : IPointPort
    {
        public int LockCount;
        public int UnfreezeCount;
        public bool ReturnFalseOnLock;

        public Task<bool> LockAsync(long customerId, string orderNo, long points, CancellationToken ct = default)
        {
            LockCount++;
            return Task.FromResult(!ReturnFalseOnLock);
        }

        public Task UnfreezeAsync(long customerId, string orderNo, CancellationToken ct = default)
        {
            UnfreezeCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeInventoryPort : IInventoryPort
    {
        public int LockCount;
        public int ReleaseCount;
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
    }

    private sealed class FakeOrderStore : IOrderStore
    {
        public Order? Saved;
        public List<Order> SavedOrders = new();
        public bool ThrowOnSave;

        public Task<Order?> FindByIdempotencyKeyAsync(
            long customerId, string idempotencyKey, CancellationToken ct = default)
            => Task.FromResult(SavedOrders.FirstOrDefault(
                a => a.CustomerId == customerId && a.IdempotencyKey == idempotencyKey));

        public Task<long> SaveAsync(Order order, IReadOnlyCollection<OrderItem> items, CancellationToken ct = default)
        {
            if (ThrowOnSave) throw new InvalidOperationException("落单失败");
            order.Id = 999;
            Saved = order;
            SavedOrders.Add(order);
            return Task.FromResult(order.Id);
        }
    }
}
