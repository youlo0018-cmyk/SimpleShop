using OrderService.Domain.Entities;

namespace OrderService.Domain.Ports;

/// <summary>下游依赖端口。定义在 Domain，编排逻辑才能在不依赖网络的情况下被单元测试。</summary>
/// <remarks>
/// 这三个端口对应下单链路的前三步（BUSINESS.md 8.1 链路 7）：
/// ① 营销占券 ② 锁定积分 ③ 锁定库存。④ 落单是本地写入。
/// </remarks>
public interface ICouponPort
{
    /// <summary>① 占券。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号。</param>
    /// <param name="couponId">券 Id，0 表示自动选最优。</param>
    /// <param name="lines">订单行，用于算券优惠。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>占用结果。没券可用时返回 0 与 0 折扣，<b>不算失败</b>。</returns>
    Task<(long CouponId, decimal Discount)> OccupyAsync(
        long customerId, string orderNo, long couponId,
        IReadOnlyList<Services.OrderLineInput> lines, CancellationToken ct = default);

    /// <summary>回退券占用（取消 / 下单回滚）。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    Task ReleaseAsync(long customerId, string orderNo, CancellationToken ct = default);
}

/// <summary>② 积分端口。</summary>
public interface IPointPort
{
    /// <summary>锁定积分。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号。</param>
    /// <param name="points">抵扣积分数（整数）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 表示锁定成功。</returns>
    Task<bool> LockAsync(long customerId, string orderNo, long points, CancellationToken ct = default);

    /// <summary>解冻积分（取消 / 回滚）。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    Task UnfreezeAsync(long customerId, string orderNo, CancellationToken ct = default);
}

/// <summary>③ 库存端口。</summary>
public interface IInventoryPort
{
    /// <summary>锁定单个 SKU 的库存。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="quantity">数量。</param>
    /// <param name="bizNo">业务单号（订单号 + SKU），幂等键的一部分。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 表示锁定成功；false 表示库存不足。</returns>
    Task<bool> LockAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default);

    /// <summary>释放库存（取消 / 回滚）。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="quantity">数量。</param>
    /// <param name="bizNo">业务单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    Task ReleaseAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default);
}

/// <summary>④ 落单端口（本地写库）。</summary>
public interface IOrderStore
{
    /// <summary>按幂等键查已有订单。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="idempotencyKey">幂等键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已存在的订单或 null。</returns>
    Task<Order?> FindByIdempotencyKeyAsync(long customerId, string idempotencyKey, CancellationToken ct = default);

    /// <summary>写入订单与订单行。</summary>
    /// <param name="order">订单主表。</param>
    /// <param name="items">订单行。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新订单 Id。</returns>
    Task<long> SaveAsync(Order order, IReadOnlyCollection<OrderItem> items, CancellationToken ct = default);
}

/// <summary>下单编排结果。</summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="OrderId">订单 Id；失败为 0。</param>
/// <param name="OrderNo">订单号；失败为 null。</param>
/// <param name="AlreadyCreated">是否命中幂等（此前已创建过同一单）。</param>
/// <param name="FailedStep">失败的步骤序号 1~4，0 表示成功。便于定位与测试断言。</param>
/// <param name="Error">失败原因。</param>
public readonly record struct OrderCreateOutcome(
    bool Succeeded, long OrderId, string? OrderNo, bool AlreadyCreated, int FailedStep, string Error)
{
    /// <summary>构造成功结果。</summary>
    public static OrderCreateOutcome Ok(long orderId, string orderNo, bool alreadyCreated = false)
        => new(true, orderId, orderNo, alreadyCreated, 0, string.Empty);

    /// <summary>构造失败结果。</summary>
    public static OrderCreateOutcome Fail(int step, string error)
        => new(false, 0, null, false, step, error);
}