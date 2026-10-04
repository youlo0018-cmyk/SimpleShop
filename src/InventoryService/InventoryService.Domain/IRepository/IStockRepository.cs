using InventoryService.Domain.Entities;

namespace InventoryService.Domain.IRepository;

/// <summary>一次库存变更。</summary>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Action">动作，见 <see cref="StockActions"/>。</param>
/// <param name="Quantity">数量。<b>带符号</b>：adjust 可以为负（减少），其余动作必须为正。</param>
/// <param name="BizNo">业务单号，幂等键的一部分。</param>
/// <param name="Remark">原因 / 备注，写进流水。</param>
/// <param name="PlatformId">平台 Id。</param>
/// <param name="MerchantId">商户 Id。</param>
public sealed record StockOperation(
    long SkuId,
    string Action,
    int Quantity,
    string BizNo,
    string Remark = "",
    long PlatformId = 0,
    long MerchantId = 0);

/// <summary>库存变更失败的分类。</summary>
/// <remarks>
/// 存在的理由：调用方（尤其是下单链路）必须能区分「货不够」与「系统/数据有问题」。
/// 两者都回 4000 的话，订单服务只能把「库存记录不存在」也当成「库存不足」，
/// 用户看到的是一句误导性的提示，排查时也看不出真正的毛病。
/// 映射到统一响应码：不足 → <c>4001 StockNotEnough</c>，其余 → <c>4000 BusinessError</c>。
/// </remarks>
public enum StockApplyFailure
{
    /// <summary>没有失败（成功或幂等命中）。</summary>
    None = 0,

    /// <summary>库存不足：变更后某个计数会变成负数。</summary>
    Shortage = 1,

    /// <summary>该 SKU 还没有库存记录，需要先初始化。</summary>
    NotInitialized = 2,

    /// <summary>请求本身不合法（数量为 0、动作未知、符号不对）。</summary>
    InvalidOperation = 3
}

/// <summary>库存变更结果。</summary>
/// <param name="Succeeded">是否已生效。</param>
/// <param name="AlreadyApplied">是否为重复请求（命中幂等，本次未真正变更）。</param>
/// <param name="Available">变更后可用。</param>
/// <param name="Locked">变更后锁定。</param>
/// <param name="Deducted">变更后已扣减。</param>
/// <param name="Error">失败原因。</param>
/// <param name="Failure">失败分类，决定对外的响应码。</param>
public sealed record StockApplyOutcome(
    bool Succeeded,
    bool AlreadyApplied,
    int Available,
    int Locked,
    int Deducted,
    string Error = "",
    StockApplyFailure Failure = StockApplyFailure.None);

/// <summary>库存仓储。</summary>
public interface IStockRepository
{
    /// <summary>原子地应用一次库存变更（幂等 + 防超卖 + 非负校验）。</summary>
    /// <param name="operation">变更描述。</param>
    /// <param name="operationId">操作人 Id，写进流水。</param>
    /// <param name="operationName">操作人姓名，写进流水。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>变更结果。</returns>
    /// <remarks>
    /// 这一个方法承担了三件事，缺一不可：
    /// <list type="bullet">
    /// <item><b>幂等</b>：靠 stock_flow 的 (biz_no, sku_id, action) 唯一键，重复请求直接返回首次结果。</item>
    /// <item><b>并发安全</b>：行锁（SELECT ... FOR UPDATE）把同一 SKU 的并发操作串行化。</item>
    /// <item><b>非负</b>：三个计数任何一个要变负都整体失败，不做部分应用。</item>
    /// </list>
    /// 所以它是**仓储方法而不是应用层拼几条 SQL**——拆开就没有原子性了。
    /// </remarks>
    Task<StockApplyOutcome> ApplyAsync(
        StockOperation operation,
        long operationId,
        string operationName,
        CancellationToken ct = default);

    /// <summary>初始化库存记录（商品创建时调用）。已存在则按编码幂等返回。</summary>
    /// <param name="operation">动作固定为 <see cref="StockActions.Init"/>。</param>
    /// <param name="productName">商品名冗余。</param>
    /// <param name="skuSpecText">规格文本冗余。</param>
    /// <param name="warnThreshold">预警阈值。</param>
    /// <param name="operationId">操作人 Id。</param>
    /// <param name="operationName">操作人姓名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>初始化结果。</returns>
    Task<StockApplyOutcome> InitAsync(
        StockOperation operation,
        string productName,
        string skuSpecText,
        int warnThreshold,
        long operationId,
        string operationName,
        CancellationToken ct = default);

    /// <summary>按 SKU Id 取库存。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>库存记录或 null。幂等只读。</returns>
    Task<Stock?> GetBySkuIdAsync(long skuId, CancellationToken ct = default);

    /// <summary>批量取库存。</summary>
    /// <param name="skuIds">SKU Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>库存列表。幂等只读。</returns>
    Task<List<Stock>> GetBySkuIdsAsync(IReadOnlyCollection<long> skuIds, CancellationToken ct = default);

    /// <summary>分页查询库存。</summary>
    /// <param name="page">页码。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="keyword">按商品名 / 规格文本模糊搜索。</param>
    /// <param name="lowStockOnly">只看低于预警阈值的。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>库存列表与总数。幂等只读。</returns>
    Task<(List<Stock> Items, long Total)> QueryPagedAsync(
        int page, int pageSize, string keyword, bool lowStockOnly, CancellationToken ct = default);

    /// <summary>取某 SKU 的全部流水。</summary>
    /// <param name="skuId">SKU Id。</param>
    /// <param name="limit">最多取多少条。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>流水列表（倒序）。幂等只读。</returns>
    Task<List<StockFlow>> GetFlowsAsync(long skuId, int limit, CancellationToken ct = default);

    /// <summary>找出疑似孤儿锁定：锁定时间已超过阈值，且没有任何后续的释放 / 扣减流水。</summary>
    /// <param name="olderThanUtc">锁定早于这个时间（UTC）才算候选。</param>
    /// <param name="limit">单次最多返回多少条。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>候选列表，按锁定时间从早到晚排。</returns>
    /// <remarks>
    /// <b>这里只管「有没有被结算过」，不管「有没有订单」</b>：那要问订单服务。
    /// 库存服务只提供候选，最终释放由调用方拿着「确实没有订单」的结论来触发——
    /// 否则库存服务就得反向依赖订单服务，两个服务耦在一起。
    ///
    /// <para><b>为什么需要它</b>：下单链路是「锁库存 → 建订单」。如果进程恰好死在两步之间，
    /// 库存就永远锁着而订单根本不存在，没有任何补偿路径能把它找回来。</para>
    /// </remarks>
    Task<List<OrphanLockCandidate>> GetOrphanLockCandidatesAsync(
        DateTime olderThanUtc, int limit, CancellationToken ct = default);

    /// <summary>按 SKU Id 集合统计各 SKU 处于锁定态的量（孤儿预留对账用）。</summary>
    /// <param name="skuIds">SKU Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>SKU Id → 锁定量。</returns>
    Task<Dictionary<long, int>> GetLockedMapAsync(IReadOnlyCollection<long> skuIds, CancellationToken ct = default);

    /// <summary>写入一条待补偿的释放记录。</summary>
    /// <param name="pending">待补偿记录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> AddPendingReleaseAsync(PendingStockRelease pending, CancellationToken ct = default);

    /// <summary>取待重试的补偿记录。</summary>
    /// <param name="nowUtc">当前 UTC 时间。</param>
    /// <param name="limit">最多取多少条。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>待处理记录列表。幂等只读。</returns>
    Task<List<PendingStockRelease>> GetDuePendingReleasesAsync(DateTime nowUtc, int limit, CancellationToken ct = default);

    /// <summary>更新补偿记录的处理状态。</summary>
    /// <param name="pending">携带 Id 与新状态的记录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> UpdatePendingReleaseAsync(PendingStockRelease pending, CancellationToken ct = default);
}
