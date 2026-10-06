using OrderService.Domain.Entities;

namespace OrderService.Domain.Ports;

/// <summary>下游依赖端口。定义在 Domain，编排逻辑才能在不依赖网络的情况下被单元测试。</summary>
/// <remarks>
/// 这三个端口对应下单链路的前三步（BUSINESS.md 8.1 链路 7）：
/// ① 营销占券 ② 锁定积分 ③ 锁定库存。④ 落单是本地写入。
/// </remarks>
/// <summary>活动优惠试算端口（下单时按行算满减 / 满折 / 满赠）。</summary>
public interface IActivityPort
{
    /// <summary>按订单行试算活动优惠。</summary>
    /// <param name="customerId">客户 Id，0 表示游客。</param>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="sessionId">秒杀场次 Id，0 表示非秒杀单。</param>
    /// <param name="couponId">客户已选的券 Id，0 表示不用券。</param>
    /// <param name="lines">订单行（SPU / SKU / 金额）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>逐行活动优惠额；算不到时返回全 0，<b>不抛异常</b>。</returns>
    /// <remarks>
    /// 结算试算会算活动优惠给前端看，但下单链路拿不到它 ——
    /// 结果是「报价 41、实收 51」，优惠凭空消失而页面无任何报错。
    ///
    /// <para><b>刻意吞掉异常</b>：营销服务不可用时按「无活动优惠」继续下单，
    /// 而不是让下单整个失败。理由是实付金额仍由订单服务按行金额自己算，
    /// 少算优惠只是少给折扣，多算才是资损。</para>
    /// </remarks>
    Task<IReadOnlyList<(long SkuId, decimal ActivityDiscount)>> QuoteAsync(
        long customerId, long platformId, long sessionId, long couponId,
        IReadOnlyList<(long SpuId, long SkuId, decimal Amount)> lines,
        CancellationToken ct = default);
}

/// <summary>券端口。定义在 Domain，编排逻辑才能在不依赖网络的情况下被单元测试。</summary>
/// <summary>商品端口。下单时回查 SKU 的**权威售价与可售状态**。</summary>
public interface IProductPort
{
    /// <summary>按 SKU Id 集合取回权威售价与状态。</summary>
    /// <param name="skuIds">SKU Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>skuId → 权威信息；查不到的 SKU 不在结果里。</returns>
    /// <remarks>
    /// <b>这是下单链路最关键的一道校验</b>。下单请求里的 <c>unitPrice</c> 来自客户端，
    /// 而订单服务此前直接拿它算金额、全程不回查商品服务 ——
    /// 于是任何人把 25.50 的商品按 0.01 元下单都能成交，整条金额链路完全由客户端决定。
    ///
    /// <para>取不到 SKU 时<b>不能</b>按「价格沿用客户端」兜底：那等于把后门重新打开。
    /// 正确做法是这一单直接失败。</para>
    /// </remarks>
    Task<IReadOnlyDictionary<long, SkuPriceInfo>> GetSkuPricesAsync(
        IReadOnlyCollection<long> skuIds, CancellationToken ct = default);
}

/// <summary>SKU 权威信息（由商品服务给出，不采信客户端）。</summary>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Price">售价，两位小数。</param>
/// <param name="Enabled">SKU 是否启用。</param>
/// <param name="SpuApproved">所属 SPU 是否审核通过。</param>
/// <param name="SpuOnShelf">所属 SPU 是否已上架。</param>
/// <param name="MerchantId">归属商户 Id。</param>
/// <param name="PlatformId">归属平台 Id。</param>
/// <param name="SkuName">商品名的权威快照（已含规格前缀）。</param>
/// <param name="SkuSpecText">规格文本的权威快照。</param>
/// <param name="Image">SKU 图。</param>
/// <param name="ProductId">SKU 真正所属的 SPU Id。</param>
/// <param name="DeliveryType">
/// 配送方式，挂在 SPU 上（BUSINESS.md 6.1）。1 实物快递 / 2 虚拟商品 / 3 实物自提。
/// </param>
/// <remarks>
/// 带上配送方式是因为**运费只对「实物快递」收**（BUSINESS.md 6.2），
/// 而配送方式此前同样由客户端上报：把自提商品报成快递就能凭空收一笔运费，
/// 反过来把快递报成自提就能白嫖免运费。
/// </remarks>
/// <para>带上 <c>PlatformId</c> 是因为<b>订单归属哪个平台必须由商品决定</b>。
/// 客户端令牌里没有 platform_id（CustomerTokenService 只签 sub 与 tenant_type），
/// 小程序于是硬编码 platformId = 0 —— 结果是平台运费永远按 0 元平台去查，
/// 后台把运费配成 10 元也一分钱收不到，订单的归属平台也全是 0。
/// 而商品服务本来就知道每个 SPU 属于哪个平台。</para>
/// <para>带上名称与规格是因为<b>订单行是最长久的对账凭据</b>，而 ProductName /
/// SkuSpecText 此前直接取客户端传来的值 —— 客户端写什么，订单就永久记什么。
/// 商家改完名之后，历史订单会显示成改名前的名字，而商品卡是另一个名字，对不上。</para>
public readonly record struct SkuPriceInfo(
    long SkuId, long ProductId, decimal Price, bool Enabled, bool SpuApproved, bool SpuOnShelf, long MerchantId,
    long PlatformId = 0, int DeliveryType = DeliveryTypes.Express,
    string SkuName = "", string SkuSpecText = "", string Image = "");

/// <summary>平台端口。运费是平台级配置，算运费的职责在订单服务，配置本身归商户平台服务。</summary>
public interface IPlatformPort
{
    /// <summary>取某平台的运费配置。</summary>
    /// <param name="platformId">平台 Id；0 表示平台自营。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>平台运费与满额包邮门槛。</returns>
    /// <remarks>
    /// <para><b>运费必须由服务端算</b>。运费是订单金额的一部分，客户端报多少就是多少的话，
    /// 小程序把 <c>freight</c> 硬编码成 0（当前前端正是如此），平台运费就一分钱都收不到 ——
    /// 后台把运费配成 10 元，顾客结算时看到的仍然是 0。</para>
    /// </remarks>
    Task<ShippingConfig> GetShippingConfigAsync(long platformId, CancellationToken ct = default);
}

/// <summary>平台运费配置。</summary>
/// <param name="ShippingFee">平台运费，仅对实物快递收取，两位小数。</param>
/// <param name="FreeShippingThreshold">满额包邮门槛，按商品实付判定；0 表示不启用。</param>
public readonly record struct ShippingConfig(decimal ShippingFee, decimal FreeShippingThreshold);


/// <summary>券端口。占券 / 核销 / 回退，与活动优惠（<see cref="IActivityPort"/>）分开。</summary>
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

    /// <summary>只读试算：列出当前可用券与各自的优惠额，<b>绝不占用</b>。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="lines">订单行。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可用券及优惠额；没有可用券时返回空集合。</returns>
    /// <remarks>
    /// 结算页要在下单**之前**把「有哪些券能用、各减多少、哪张最优惠」告诉用户
    /// （用户需求 K9），所以这里必须是只读的。
    /// 复用 <c>OccupyAsync</c> 来试算会在用户每看一眼结算页时就锁掉一张券 ——
    /// 看了三次页面，客户的券就被占没了。
    /// </remarks>
    Task<IReadOnlyList<CouponQuoteOption>> QuoteAsync(
        long customerId, IReadOnlyList<CouponPortLine> lines, CancellationToken ct = default);
}

/// <summary>一张可用券在当前订单下的试算结果。</summary>
/// <param name="CouponId">用户券 Id。</param>
/// <param name="CouponTypeName">券类型中文名（满减 / 折扣）。</param>
/// <param name="DiscountAmount">该券在本单可减的金额，两位小数。</param>
/// <param name="ExpireAt">过期时间，用于「同等优惠优先临期」的排序展示。</param>
/// <param name="IsBest">是否是最优券（同等优惠优先临期）。</param>
public readonly record struct CouponQuoteOption(
    long CouponId, string CouponTypeName, decimal DiscountAmount, string ExpireAt, bool IsBest);

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

    /// <summary>退款按比例回收该单已扣积分（向上取整，退回原冻结批次）。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="orderNo">订单号，幂等键的一部分。</param>
    /// <param name="refundRatio">退款比例 0~1，整单退款传 1，部分退款传本次退款额 ÷ 实付。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 积分侧的实现在 PointService 已就绪（<c>internal/points/Refund</c>），
    /// 这里缺的只是**调用方**：退款链路走完却没人通知积分服务，
    /// 结果是客户一边拿回钱、一边把抵扣的积分白留着 —— 双花。
    /// 比例由调用方算好：只有退款方知道退了多少，积分服务不认订单金额。
    /// </remarks>
    Task RecoverByRefundAsync(
        long customerId, string orderNo, decimal refundRatio, CancellationToken ct = default);

}

/// <summary>秒杀端口。秒杀单退款时把货退回秒杀池。</summary>
public interface ISeckillPort
{
    /// <summary>把货退回**秒杀池**。</summary>
    /// <param name="customerId">下单客户 Id。</param>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="quantity">退回件数。</param>
    /// <param name="orderNo">订单号，用于日志对账。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 秒杀库存是**发布场次时从常规池划走**的，秒杀单从未锁过常规库存，
    /// 所以退款绝不能走常规库存的 release —— 那笔锁定不存在，必然失败；
    /// 正确做法是把 <c>sold_count</c> 减回去，货由场次结束时的
    /// 「seckill_stock − sold_count」自然回到常规池。
    /// </remarks>
    Task ReleaseGrabAsync(
        long customerId, long skuId, int quantity, string orderNo, CancellationToken ct = default);
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

/// <summary>订单聚合结果，供工作台报表使用。</summary>
/// <param name="OrderCount">下单数。</param>
/// <param name="PaidOrderCount">支付订单数。</param>
/// <param name="CompletedOrderCount">完成订单数。</param>
/// <param name="Gmv">成交额。</param>
public sealed record OrderAggregateRow(
    long OrderCount, long PaidOrderCount, long CompletedOrderCount, decimal Gmv);

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

    /// <summary>按 Id 取订单（后台详情用）。</summary>
    /// <param name="orderId">订单 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单；不存在返回 null。</returns>
    /// <remarks>
    /// 后台列表页拿到的是 Id（列表接口不下发订单号给链接用），点进详情时按 Id 查。
    /// 不复用 <see cref="FindByOrderNoAsync"/>：为了查一条单先扫一遍订单号索引不划算。
    /// </remarks>
    Task<Order?> GetByIdAsync(long orderId, CancellationToken ct = default);

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

    /// <summary>找出创建时间早于某时刻、且仍处于待支付的订单。</summary>
    /// <param name="deadlineUtc">创建时间的上界（UTC）。传入「现在减去超时阈值」。</param>
    /// <param name="limit">最多返回多少张。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>候选订单，按创建时间<b>升序</b>（先关最早的）。</returns>
    /// <remarks>
    /// 升序很重要：积压时如果随机取一批，可能出现「关了新的、留了更老的」，
    /// 而老的那些正是用户等最久、最可能在投诉的。
    /// </remarks>
    Task<List<Order>> FindTimeoutCandidatesAsync(
        DateTime deadlineUtc, int limit, CancellationToken ct = default);

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
    /// <param name="customerId">客户 Id，0 表示不限。</param>
    /// <param name="customerNo">客户唯一编码，空表示不限。</param>
    /// <param name="from">下单时间下界 UTC，null 表示不限。</param>
    /// <param name="to">下单时间上界 UTC，null 表示不限。</param>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当页订单与总条数。</returns>
    Task<(List<Order> Orders, long Total)> ListAsync(
        int status, string keyword, long platformId, long merchantId,
        long customerId, string customerNo, DateTime? from, DateTime? to,
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
    /// <param name="paidAt">同时写入的支付时间，null 表示不改。</param>
    /// <param name="completedAt">同时写入的完成时间，null 表示不改。</param>
    /// <returns>受影响行数；为 0 表示状态已被别人改过，本次不生效。</returns>
    /// <remarks>
    /// <b>并发控制靠的就是这个「受影响行数为 0」</b>，不是行锁：
    /// 条件里带上了读到的原状态，两个并发请求只有一个能把状态改掉。
    /// 客户端看到 0 就该回「订单状态已变更，请刷新」。
    ///
    /// <para><b>时间戳必须在同一条 UPDATE 里写</b>：拆成「先改状态、再单独更新 paid_at」
    /// 的话，两步之间进程死掉就会留下一张「已支付但 paid_at 为 null」的订单，
    /// 报表按支付时间统计时它会**从所有区间里消失**——GMV 凭空少一块，
    /// 而订单状态看上去完全正常。</para>
    ///
    /// <para><b>ct 保持在第 4 位</b>：它后面再追加可选参数时，
    /// 新参数只能排在 ct 之后。反过来（把可选参数插到 ct 前面）会要求
    /// 所有位置传参的调用点改成具名参数，一次改动波及十几个文件，
    /// 而收益仅仅是「新参数出现在签名更靠前的位置」。</para>
    /// </remarks>
    Task<int> TryTransitStatusAsync(
        long orderId, int fromStatus, int toStatus,
        CancellationToken ct = default,
        DateTime? paidAt = null, DateTime? completedAt = null);

    /// <summary>
    /// 发货：条件更新状态并同时写入物流信息。
    /// </summary>
    /// <param name="orderId">订单 Id。</param>
    /// <param name="fromStatus">期望的原状态，必须是待发货。</param>
    /// <param name="toStatus">目标状态，待收货。</param>
    /// <param name="logisticsCompanyId">物流公司 Id。</param>
    /// <param name="logisticsCompanyName">物流公司名称快照。</param>
    /// <param name="trackingNo">运单号。</param>
    /// <param name="shippedAt">发货时间 UTC。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数；为 0 表示状态已被别人改过，本次不生效。</returns>
    /// <remarks>
    /// <b>单独一个方法而不是给 <see cref="TryTransitStatusAsync"/> 再加四个可选参数</b>：
    /// 物流四列只在发货这一条路径上写，加进通用方法会让「确认收货」也能顺手改掉
    /// 别人的运单号 —— 而这种错误在页面上完全看不出来。
    /// 状态与物流信息必须在**同一条 UPDATE** 里写：拆开的话中间崩掉会留下一张
    /// 「已发货但没有运单号」的订单，客服拿着空单号去查物流永远查不到。
    /// </remarks>
    Task<int> TryShipAsync(
        long orderId, int fromStatus, int toStatus,
        long logisticsCompanyId, string logisticsCompanyName, string trackingNo,
        DateTime shippedAt, CancellationToken ct = default);

    /// <summary>按订单行聚合已退数量与已退金额。</summary>
    /// <param name="orderId">订单 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>orderItemId → (已退数量, 已退金额)。没有退过的行不在结果里。</returns>
    /// <remarks>
    /// 部分退款的行级余额校验靠它：行实付 − 行已退 = 还能退多少。
    /// 一次查完而不是每行查一次 —— 一张单最多 50 行，逐行查就是 50 条 SQL。
    /// </remarks>
    Task<IReadOnlyDictionary<long, RefundedItemBalance>> AggregateRefundedItemsAsync(
        long orderId, CancellationToken ct = default);

    /// <summary>写入一条退款记录及其明细（同事务）。</summary>
    /// <param name="refund">退款记录主表。</param>
    /// <param name="items">退款明细。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>退款记录 Id。</returns>
    /// <remarks>
    /// 主表与明细必须在同一事务：只写主表的话，订单详情页会显示一条
    /// 「退了 100 元」但没有任何商品的记录，客服根本不知道退的是哪几件。
    /// </remarks>
    Task<long> SaveRefundAsync(
        OrderRefund refund, IReadOnlyCollection<OrderRefundItem> items, CancellationToken ct = default);

    /// <summary>累加已退金额，并在退完时把订单打成已退款。</summary>
    /// <param name="orderId">订单 Id。</param>
    /// <param name="fromStatus">期望的原状态。</param>
    /// <param name="amount">本次退款金额。</param>
    /// <param name="fullyRefunded">退完后是否剩余可退余额为 0。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数；为 0 表示状态已变或已退金额超出余额。</returns>
    /// <remarks>
    /// <b>余额判断必须落在 SQL 的 WHERE 里</b>（<c>refunded_amount + amount &lt;= payable_amount</c>）：
    /// 只在 C# 里判断的话，两个并发退款请求都会读到同一个旧的 refunded_amount，
    /// 都认为「还有余额」，然后一起把订单退成超额 —— 这是实打实的资损。
    /// 让数据库做这条判断，受影响行数为 0 就是「被别人抢先了」，调用方据此回错。
    /// </remarks>
    Task<int> TryApplyRefundAsync(
        long orderId, int fromStatus, decimal amount, bool fullyRefunded,
        CancellationToken ct = default);

    /// <summary>按订单 Id 取全部退款记录与明细（后台订单详情用）。</summary>
    /// <param name="orderId">订单 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>退款记录，按发生时间升序；每条带上它的明细行。</returns>
    /// <remarks>
    /// 一张订单可以有多条（多次部分退款），所以这里返回列表而不是单条。
    /// 详情页要按时间顺序展示，否则运营看到的「上次退了多少」是错的。
    /// </remarks>
    Task<IReadOnlyList<(OrderRefund Refund, IReadOnlyList<OrderRefundItem> Items)>> ListRefundsAsync(
        long orderId, CancellationToken ct = default);

    /// <summary>按区间聚合订单指标，供工作台报表使用。</summary>
    /// <param name="from">区间起（含）。</param>
    /// <param name="to">区间止（不含）。</param>
    /// <param name="merchantId">商户 Id，0 表示不限。</param>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下单数 / 支付订单数 / 完成订单数 / 成交额。</returns>
    /// <remarks>
    /// <b>成交额按 paid_at 统计，其余按 created_at</b>。
    /// 混用同一个时间基准是报表最常见的错：按下单时间算 GMV，
    /// 会把「昨天下单今天付款」算进昨天，而昨天日报里这笔钱根本没收过。
    ///
    /// <para><b>历史订单的 paid_at 可能为空</b>（该列是后加的），
    /// 所以按支付时间统计时要用 COALESCE(paid_at, created_at) 兜底，
    /// 否则上线前的订单会从所有报表区间里凭空消失。</para>
    /// </remarks>
    Task<OrderAggregateRow> AggregateAsync(
        DateTime from, DateTime to, long merchantId, long platformId,
        CancellationToken ct = default);

    /// <summary>按订单号集合汇总成交额，供秒杀效果报表使用。</summary>
    /// <param name="orderNos">订单号集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已支付（不含已取消 / 已退款）的实付合计。</returns>
    /// <remarks>
    /// <b>口径与工作台 GMV 完全一致</b>：排除待支付、已取消、已退款。
    /// 两处口径一旦不同，运营把「秒杀 GMV」加到「工作台 GMV」里对不上，
    /// 而数字本身看着都合理，只能靠逐单核对才发现。
    /// </remarks>
    Task<decimal> SumPayableByOrderNosAsync(
        IReadOnlyCollection<string> orderNos, CancellationToken ct = default);
}

/// <summary>订单行的聚合信息，供列表页一次取齐。</summary>
/// <param name="OrderId">订单 Id。</param>
/// <param name="ItemQuantity">总件数（各行数量之和）。</param>
/// <param name="LineCount">商品行数（规格种类数）。</param>
/// <param name="FirstProductName">第一个商品名，用于列表页缩略文字。</param>
/// <param name="HasPhysical">是否含实物快递行，列表页据此显示「发货」按钮。</param>
/// <param name="HasVirtual">是否含虚拟商品行，含虚拟行时整单不可退款。</param>
/// <param name="HasSelfPickup">是否含自提行，列表页据此显示「核销」按钮。</param>
public readonly record struct OrderItemAggregate(
    long OrderId,
    int ItemQuantity,
    int LineCount,
    string FirstProductName,
    bool HasPhysical,
    bool HasVirtual,
    bool HasSelfPickup);

/// <summary>订单行已退余额。</summary>
/// <param name="OrderItemId">订单行 Id。</param>
/// <param name="Quantity">已退数量。</param>
/// <param name="Amount">已退金额。</param>
public readonly record struct RefundedItemBalance(long OrderItemId, int Quantity, decimal Amount);

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

/// <summary>退款统计端口（工作台报表用，数据在支付服务）。</summary>
public interface IRefundStatsPort
{
    /// <summary>按区间汇总审批通过的退款金额。</summary>
    /// <param name="from">区间起（含）。</param>
    /// <param name="to">区间止（不含）。</param>
    /// <param name="merchantId">商户 Id，0 表示不限。</param>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>退款金额合计。</returns>
    /// <remarks>
    /// <b>支付服务不可用时返回 0 而不是抛异常</b>：报表少一个数字，
    /// 比整个工作台打不开要好。真正的故障由支付服务自己的日志暴露，
    /// 这里不该让一个附属指标把主页面一起拖死。
    /// </remarks>
    Task<decimal> SumApprovedAsync(
        DateTime from, DateTime to, long merchantId, long platformId, CancellationToken ct = default);
}

/// <summary>库存预警数端口（工作台报表用，数据在库存服务）。</summary>
public interface ILowStockPort
{
    /// <summary>统计低于预警阈值的 SKU 数。</summary>
    /// <param name="merchantId">商户 Id，0 表示不限。</param>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>预警 SKU 数。库存服务不可用时返回 0。</returns>
    /// <remarks>同 <see cref="IRefundStatsPort"/>：附属指标不该拖垮主页面。</remarks>
    Task<int> CountLowStockAsync(long merchantId, long platformId, CancellationToken ct = default);
}

/// <summary>物流公司查询端口（发货时取公司名快照）。</summary>
/// <remarks>
/// 物流公司字典归商品服务管，而订单服务要往订单上写一份公司名快照，
/// 所以这里走一次内网查询而不是让前端把名字传上来 ——
/// 前端传的名字可以随便编，订单上就会留下一条查无此公司的物流记录。
/// </remarks>
public interface ILogisticsCompanyPort
{
    /// <summary>按 Id 取物流公司名称。</summary>
    /// <param name="logisticsCompanyId">物流公司 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>公司名；查不到返回 null（调用方据此拒绝发货）。</returns>
    Task<string?> ResolveNameAsync(long logisticsCompanyId, CancellationToken ct = default);
}
