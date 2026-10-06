using MarketingService.Domain.Entities;
using MarketingService.Domain.Services;

namespace MarketingService.Domain.IRepository;

/// <summary>秒杀仓储。</summary>
public interface ISeckillRepository
{
    /// <summary>按 Id 取场次。</summary>
    /// <param name="sessionId">场次 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>场次或 null。</returns>
    Task<SeckillSession?> GetSessionAsync(long sessionId, CancellationToken ct = default);

    /// <summary>分页查场次（后台）。</summary>
    /// <param name="status">状态，0 表示不限。</param>
    /// <param name="page">页码。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当页场次与总条数。</returns>
    Task<(List<SeckillSession> Items, long Total)> ListSessionsAsync(
        int status, int page, int pageSize, CancellationToken ct = default);

    /// <summary>查当前可展示的场次（前台）。</summary>
    /// <param name="nowUtc">当前时间。</param>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>未开始（即将开场）与进行中的场次。</returns>
    /// <remarks>
    /// 前台只看得到「即将开场」与「进行中」两类。已结束 / 已取消的场次
    /// 对用户没有意义（没有货可抢），但**不能直接删**——历史订单要能查到秒杀价来源。
    /// </remarks>
    Task<List<SeckillSession>> ListPublicSessionsAsync(
        DateTime nowUtc, long platformId, CancellationToken ct = default);

    /// <summary>查「到点该结束但还没结束」的场次（定时任务用）。</summary>
    /// <param name="nowUtc">当前时间。</param>
    /// <param name="limit">最多取多少条。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已过结束时间但仍是「进行中」的场次。</returns>
    /// <remarks>
    /// 这个查询是<b>定时结束场次</b>的唯一入口。少了它，一个到点没人管的场次
    /// 会永远停在「进行中」：剩余库存永久锁在秒杀池里，常规库存再也回不来，
    /// 而且没有任何报错——商品只是「一直缺货」，排查时完全看不出原因。
    /// </remarks>
    Task<List<SeckillSession>> ListExpiredRunningSessionsAsync(
        DateTime nowUtc, int limit, CancellationToken ct = default);

    /// <summary>插入场次。</summary>
    /// <param name="session">场次。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新场次 Id。</returns>
    Task<long> InsertSessionAsync(SeckillSession session, CancellationToken ct = default);

    /// <summary>更新场次（不含库存划转）。</summary>
    /// <param name="session">场次，需带 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> UpdateSessionAsync(SeckillSession session, CancellationToken ct = default);

    /// <summary>改场次状态。</summary>
    /// <param name="sessionId">场次 Id。</param>
    /// <param name="fromStatus">期望的当前状态。</param>
    /// <param name="toStatus">目标状态。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数；0 表示状态已被别人改过。</returns>
    /// <remarks>
    /// 条件更新（带上期望的当前状态）是并发控制：
    /// 两个「结束场次」同时点，只有一个能改成功，另一个拿到 0 就知道该跳过回补。
    /// </remarks>
    Task<int> TrySetSessionStatusAsync(
        long sessionId, int fromStatus, int toStatus, CancellationToken ct = default);

    /// <summary>标记库存已划出 / 已回补。</summary>
    /// <param name="sessionId">场次 Id。</param>
    /// <param name="transferred">true 表示已划出。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> SetStockTransferredAsync(
        long sessionId, bool transferred, CancellationToken ct = default);

    /// <summary>取场次内的商品。</summary>
    /// <param name="sessionId">场次 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品列表。</returns>
    Task<List<SeckillItem>> ListItemsAsync(long sessionId, CancellationToken ct = default);

    /// <summary>按 SKU 找出它参与过的全部秒杀商品（跨场次）。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的秒杀商品，按创建时间倒序（最近一个排最前）。</returns>
    /// <remarks>
    /// 退款要靠它把「这笔秒杀订单的货退回秒杀池」——
    /// 订单上只记了 <c>skuId</c>，没有记秒杀商品 Id，只能由 SKU 反查。
    /// 一个 SKU 可以参加多个场次，所以返回列表而不是单条。
    /// </remarks>
    Task<List<SeckillItem>> ListItemsBySkuAsync(long skuId, CancellationToken ct = default);

    /// <summary>按场次聚合秒杀效果，供秒杀效果报表使用。</summary>
    /// <param name="from">场次开始时间下界（含），DateTime.MinValue 表示不限。</param>
    /// <param name="to">场次开始时间上界（不含），DateTime.MaxValue 表示不限。</param>
    /// <param name="sessionId">只看某个场次，0 表示全部。</param>
    /// <param name="merchantId">商户 Id，0 表示不限。</param>
    /// <param name="platformId">平台 Id，0 表示不限。</param>
    /// <param name="limit">最多返回多少个场次。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>逐场次的参与人数、抢购成功数、售罄率与订单号清单。</returns>
    /// <remarks>
    /// <b>「场次 PV」刻意不做</b>：前台只有一个 <c>POST /marketing/seckill/sessions/Public</c>，
    /// <b>sessionId 在请求体里</b>，而 pv 日志只记录 path / queryString / method，
    /// 请求体拿不到，所以无法把页面访问按场次归因。
    /// 与其返回一个「所有场次都一样」的假数字，不如不提供。
    /// </remarks>
    Task<List<SeckillSessionAggregate>> AggregateSessionsAsync(
        DateTime from, DateTime to, long sessionId, long merchantId, long platformId,
        int limit, CancellationToken ct = default);

    /// <summary>按 Id 取场次商品。</summary>
    /// <param name="itemId">商品 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品或 null。</returns>
    Task<SeckillItem?> GetItemAsync(long itemId, CancellationToken ct = default);

    /// <summary>插入场次商品。</summary>
    /// <param name="item">商品。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新商品 Id。</returns>
    Task<long> InsertItemAsync(SeckillItem item, CancellationToken ct = default);

    /// <summary>更新场次商品。</summary>
    /// <param name="item">商品，需带 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> UpdateItemAsync(SeckillItem item, CancellationToken ct = default);

    /// <summary>软删场次商品。</summary>
    /// <param name="itemId">商品 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> SoftDeleteItemAsync(long itemId, CancellationToken ct = default);

    /// <summary>
    /// 条件增加已抢数量——<b>防超卖的核心</b>。
    /// </summary>
    /// <param name="itemId">商品 Id。</param>
    /// <param name="quantity">增加的数量。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。<b>返回 0 表示已抢完，本次没抢到。</b></returns>
    /// <remarks>
    /// WHERE 条件里带 <c>sold_count + quantity &lt;= seckill_stock</c>，
    /// 于是两个并发请求只有一个能把 sold_count 推上去，另一个拿到 0。
    ///
    /// 这比「先查 sold 再判断再写」可靠得多：后者两个请求会同时读到同一个 sold，
    /// 都判断还有货，然后都写回去——超卖就是这么来的。
    ///
    /// 数据库上还有一条 <c>CHECK (sold_count &lt;= seckill_stock)</c> 兜底，
    /// Redis 预扣是第一道防线，这里是第二道，数据库约束是第三道。
    /// </remarks>
    Task<int> TryIncreaseSoldAsync(long itemId, int quantity, CancellationToken ct = default);

    /// <summary>回补已抢数量（下单失败时）。</summary>
    /// <param name="itemId">商品 Id。</param>
    /// <param name="quantity">回补的数量。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    /// <remarks>同样用条件更新，保证不会回补成负数。</remarks>
    Task<int> TryDecreaseSoldAsync(long itemId, int quantity, CancellationToken ct = default);

    /// <summary>写入抢购请求。</summary>
    /// <param name="grab">抢购记录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新记录 Id。</returns>
    /// <exception cref="DuplicateGrabException">命中幂等唯一索引（同一客户重复抢同一商品）。</exception>
    Task<long> InsertGrabAsync(SeckillGrab grab, CancellationToken ct = default);

    /// <summary>按业务号取抢购记录。</summary>
    /// <param name="bizNo">幂等键 <c>{itemId}:{customerId}</c>。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>记录或 null。</returns>
    Task<SeckillGrab?> GetGrabByBizNoAsync(string bizNo, CancellationToken ct = default);

    /// <summary>按请求 Id 取抢购记录。</summary>
    /// <param name="requestId">请求 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>记录或 null。</returns>
    Task<SeckillGrab?> GetGrabByRequestIdAsync(string requestId, CancellationToken ct = default);

    /// <summary>更新抢购结果。</summary>
    /// <param name="grab">抢购记录，需带 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> UpdateGrabAsync(SeckillGrab grab, CancellationToken ct = default);
}

/// <summary>同一客户重复抢购同一场次商品时抛出。</summary>
/// <remarks>
/// 单独一个异常类型而不是直接吞掉唯一键冲突：调用方要靠它区分
/// 「超限购」（正常业务，提示用户）与「真的写库失败」（故障）。
/// </remarks>
public sealed class DuplicateGrabException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="bizNo">幂等键。</param>
    public DuplicateGrabException(string bizNo)
        : base($"已存在同一客户的抢购记录：{bizNo}")
    {
        BizNo = bizNo;
    }

    /// <summary>幂等键。</summary>
    public string BizNo { get; }
}
