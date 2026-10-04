namespace InventoryService.Domain.IRepository;

/// <summary>疑似孤儿锁定：锁了但迟迟没有对应订单、也没被释放 / 扣减。</summary>
/// <param name="BizNo">业务单号，格式 <c>{订单号}:{skuId}</c>。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Quantity">锁定数量。</param>
/// <param name="LockedAt">锁定时间 UTC。</param>
public sealed record OrphanLockCandidate(string BizNo, long SkuId, int Quantity, DateTime LockedAt);
