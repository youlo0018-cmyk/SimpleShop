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
    /// <param name="orderNo">订单号，也是占券的幂等键。</param>
    /// <param name="couponId">券 Id，0 表示自动选最优。</param>
    /// <param name="lines">订单行，用于算券优惠。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>占用结果。没券可用时返回 0 与 0 折扣，<b>不算失败</b>。</returns>
    Task<(long CouponId, decimal Discount)> OccupyAsync(
        long customerId, string orderNo, long couponId,
        IReadOnlyList<CouponPortLine> lines, CancellationToken ct = default);

    /// <summary>回退券占用（取消 / 下单回滚）。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    Task ReleaseAsync(long customerId, string orderNo, CancellationToken ct = default);

    /// <summary>核销券（支付成功）。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    Task ConsumeAsync(long customerId, string orderNo, CancellationToken ct = default);
}

/// <summary>② 积分端口。</summary>
public interface IPointPort
{
    /// <summary>锁定积分。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号，也是冻结的幂等键。</param>
    /// <param name="points">抵扣积分数（整数）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 表示锁定成功；false 表示可用积分不足。</returns>
    Task<bool> LockAsync(long customerId, string orderNo, long points, CancellationToken ct = default);

    /// <summary>解冻积分（取消 / 回滚）。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    Task UnfreezeAsync(long customerId, string orderNo, CancellationToken ct = default);

    /// <summary>实扣积分（支付成功后由支付链路调用，不在下单编排内）。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    Task ConsumeAsync(long customerId, string orderNo, CancellationToken ct = default);

    /// <summary>订单完成时按实付金额发放积分。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号，同时是发放的幂等键。</param>
    /// <param name="paidAmount">订单实付金额，两位小数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 只传<b>实付金额</b>、不传算好的积分数：「实付每满 1 元 1 积分」这条规则
    /// 必须只在积分服务里存在一处。两边各算一遍的话，改了规则就会有两个数，
    /// 而且没有任何测试会发现。
    /// </remarks>
    Task EarnByOrderAsync(long customerId, string orderNo, decimal paidAmount, CancellationToken ct = default);
}

/// <summary>③ 库存端口。</summary>
public interface IInventoryPort
{
    /// <summary>锁定单个 SKU 的库存。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="quantity">数量。</param>
    /// <param name="bizNo">业务单号（订单号 + SKU），幂等键的一部分。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 表示锁定成功；false 表示可用库存不足。</returns>
    Task<bool> LockAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default);

    /// <summary>释放库存（取消 / 回滚）。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="quantity">数量。</param>
    /// <param name="bizNo">业务单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    Task ReleaseAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default);

    /// <summary>扣减库存（支付成功：locked → deducted）。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="quantity">数量。</param>
    /// <param name="bizNo">业务单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 幂等键与 <see cref="LockAsync"/> 共用同一个 <paramref name="bizNo"/>，
    /// 只靠下游流水里的 action 区分「锁定」与「扣减」两条记录，
    /// 所以支付回调重投多少次都只会真正扣一次。
    /// </remarks>
    Task DeductAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default);

    /// <summary>退款回补库存（deducted → available）。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="quantity">数量。</param>
    /// <param name="bizNo">业务单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 只在<b>已扣减</b>阶段退款时用（已发货的订单）。还在锁定阶段的订单退款要走
    /// <see cref="ReleaseAsync"/>：一个是还 locked，一个是还 deducted，用错会把某个计数减成负数。
    /// </remarks>
    Task ReplenishAsync(long skuId, int quantity, string bizNo, CancellationToken ct = default);
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

    /// <summary>按订单号取订单。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单或 null。</returns>
    Task<Order?> FindByOrderNoAsync(string orderNo, CancellationToken ct = default);

    /// <summary>写入订单与订单行（同事务）。</summary>
    /// <param name="order">订单主表。</param>
    /// <param name="items">订单行。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>
    /// <b>权威的订单实体</b>：正常写入时就是传入的那张（已填 Id、CreatedAt）；
    /// 若撞上幂等键唯一索引、说明并发下已有同一张单，则返回<b>已存在的那张</b>。
    /// 调用方必须用返回值里的 Id 与 OrderNo，不要继续用自己生成的那个——
    /// 后者在冲突分支下是废号，返回给用户就成了一张查不到的订单。
    /// </returns>
    Task<Order> SaveAsync(Order order, IReadOnlyCollection<OrderItem> items, CancellationToken ct = default);

    /// <summary>取订单的全部订单行。</summary>
    /// <param name="orderId">订单 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单行，按行 Id 升序。</returns>
    Task<List<OrderItem>> ListItemsAsync(long orderId, CancellationToken ct = default);

    /// <summary>分页查某个客户的订单。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="status">订单状态，0 表示不限。</param>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当页订单与总条数。</returns>
    Task<(List<Order> Orders, long Total)> ListByCustomerAsync(
        long customerId, int status, int page, int pageSize, CancellationToken ct = default);

    /// <summary>分页查订单（后台用）。</summary>
    /// <param name="status">订单状态，0 表示不限。</param>
    /// <param name="keyword">按订单号 / 收货人 / 手机号模糊匹配，空表示不过滤。</param>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="merchantId">商户 Id，0 表示不限。</param>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当页订单与总条数。</returns>
    Task<(List<Order> Orders, long Total)> ListAsync(
        int status, string keyword, long platformId, long merchantId,
        int page, int pageSize, CancellationToken ct = default);

    /// <summary>按订单 Id 集合取聚合信息（件数、首个商品名）。</summary>
    /// <param name="orderIds">订单 Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>orderId → 聚合信息；没有订单行的订单不会出现在结果里。</returns>
    /// <remarks>
    /// 订单列表要显示「共 N 件」，但件数在 order_item 上，
    /// 没有这张表就只能 N+1 逐单查——列表页一次 20 单就是 20 条额外 SQL。
    /// </remarks>
    Task<Dictionary<long, OrderItemAggregate>> AggregateItemsAsync(
        IReadOnlyCollection<long> orderIds, CancellationToken ct = default);

    /// <summary>条件更新订单状态。</summary>
    /// <param name="orderId">订单 Id。</param>
    /// <param name="fromStatus">期望的原状态。</param>
    /// <param name="toStatus">目标状态。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数；为 0 表示状态已被别人改过，本次不生效。</returns>
    /// <remarks>
    /// <b>并发控制靠的就是这个「受影响行数为 0」</b>，不是行锁：
    /// 条件里带上了读到的原状态，两个并发请求只有一个能把状态改掉。
    /// 客户端看到 0 就该回「订单状态已变更，请刷新」。
    /// </remarks>
    Task<int> TryTransitStatusAsync(
        long orderId, int fromStatus, int toStatus, CancellationToken ct = default);
}

/// <summary>订单行的聚合信息，供列表页一次取齐。</summary>
/// <param name="OrderId">订单 Id。</param>
/// <param name="ItemQuantity">总件数（各行数量之和）。</param>
/// <param name="LineCount">商品行数（规格种类数）。</param>
/// <param name="FirstProductName">第一个商品名，用于列表页缩略文字。</param>
public readonly record struct OrderItemAggregate(long OrderId, int ItemQuantity, int LineCount, string FirstProductName);

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