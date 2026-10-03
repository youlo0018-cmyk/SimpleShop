using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace InventoryService.Domain.Entities;

/// <summary>库存。SKU 维度，一个 SKU 一行。</summary>
/// <remarks>
/// <b>三个计数必须分开，不能合并成一个「库存」字段</b>：
/// available（可被锁定）/ locked（下单占用未支付）/ deducted（已支付）。
/// 合并之后就分不清「还没卖掉」和「卖掉但没发货」，退款回补与超时对账全都做不了。
/// 这正是 BUSINESS.md 9.1 把它们拆成三个字段的原因。
/// </remarks>
[Table(Name = "stock")]
public class Stock : AdminEntityBase
{
    /// <summary>SKU Id。一个 SKU 只有一条未删除的库存记录。</summary>
    [Column(Name = "sku_id")]
    public long SkuId { get; set; }

    /// <summary>商品名冗余。库存列表直接显示，不必回 ProductService 查一次。</summary>
    [Column(Name = "product_name", StringLength = 128)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格文本冗余（如「红色 / M」），人工辨认库存时用。</summary>
    [Column(Name = "sku_spec_text", StringLength = 256)]
    public string SkuSpecText { get; set; } = string.Empty;

    /// <summary>可用库存（可被锁定）。不得为负。</summary>
    [Column(Name = "available")]
    public int Available { get; set; }

    /// <summary>锁定库存（下单占用，未支付）。不得为负。</summary>
    [Column(Name = "locked")]
    public int Locked { get; set; }

    /// <summary>已扣减库存（已支付）。不得为负。</summary>
    [Column(Name = "deducted")]
    public int Deducted { get; set; }

    /// <summary>库存预警阈值。available 低于它时列表高亮。</summary>
    [Column(Name = "warn_threshold")]
    public int WarnThreshold { get; set; }

    /// <summary>是否低于预警阈值。</summary>
    public bool IsLowStock => WarnThreshold > 0 && Available < WarnThreshold;
}

/// <summary>库存流水。<b>幂等就靠这张表</b>（BUSINESS.md 9.3）。</summary>
/// <remarks>
/// 唯一键是 (biz_no, sku_id, action)，重复请求会撞唯一键，
/// 插入失败即识别为「这个业务号已经处理过」，直接返回首次的结果。
/// 必须用数据库约束而不是「先查再插」：两个并发请求可能都查不到，然后都插进去。
/// </remarks>
[Table(Name = "stock_flow")]
public class StockFlow : EntityBase
{
    /// <summary>业务单号（订单号 / 秒杀场次号 / 手工调整单号）。</summary>
    [Column(Name = "biz_no", StringLength = 64)]
    public string BizNo { get; set; } = string.Empty;

    /// <summary>SKU Id。</summary>
    [Column(Name = "sku_id")]
    public long SkuId { get; set; }

    /// <summary>动作，见 <see cref="StockActions"/>。</summary>
    [Column(Name = "action", StringLength = 32)]
    public string Action { get; set; } = string.Empty;

    /// <summary>数量，正数。方向由 action 决定。</summary>
    [Column(Name = "quantity")]
    public int Quantity { get; set; }

    /// <summary>变更前可用。</summary>
    [Column(Name = "before_available")]
    public int BeforeAvailable { get; set; }

    /// <summary>变更后可用。</summary>
    [Column(Name = "after_available")]
    public int AfterAvailable { get; set; }

    /// <summary>变更前锁定。</summary>
    [Column(Name = "before_locked")]
    public int BeforeLocked { get; set; }

    /// <summary>变更后锁定。</summary>
    [Column(Name = "after_locked")]
    public int AfterLocked { get; set; }

    /// <summary>变更前已扣减。</summary>
    [Column(Name = "before_deducted")]
    public int BeforeDeducted { get; set; }

    /// <summary>变更后已扣减。</summary>
    [Column(Name = "after_deducted")]
    public int AfterDeducted { get; set; }

    /// <summary>原因 / 备注。</summary>
    [Column(Name = "remark", StringLength = 512)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>操作人 Id。</summary>
    [Column(Name = "operation_id")]
    public long OperationId { get; set; }

    /// <summary>操作人姓名。</summary>
    [Column(Name = "operation_name", StringLength = 64)]
    public string OperationName { get; set; } = string.Empty;

    /// <summary>平台 Id（租户）。</summary>
    [Column(Name = "platform_id")]
    public long PlatformId { get; set; }

    /// <summary>商户 Id（租户）。</summary>
    [Column(Name = "merchant_id")]
    public long MerchantId { get; set; }
}

/// <summary>补偿表：释放失败写这里，ScheduledService 每轮重试。</summary>
[Table(Name = "pending_stock_release")]
public class PendingStockRelease : EntityBase
{
    /// <summary>原业务单号。重试时会加后缀防重，避免与首次记录撞幂等键。</summary>
    [Column(Name = "biz_no", StringLength = 64)]
    public string BizNo { get; set; } = string.Empty;

    /// <summary>SKU Id。</summary>
    [Column(Name = "sku_id")]
    public long SkuId { get; set; }

    /// <summary>要释放的数量。</summary>
    [Column(Name = "quantity")]
    public int Quantity { get; set; }

    /// <summary>失败原因。</summary>
    [Column(Name = "reason", StringLength = 512)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>处理状态，见 <see cref="PendingReleaseStatus"/>。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = PendingReleaseStatus.Pending;

    /// <summary>已重试次数。超过上限就不再重试，否则一条坏数据会被无限重试。</summary>
    [Column(Name = "retry_count")]
    public int RetryCount { get; set; }

    /// <summary>最近一次失败详情。</summary>
    [Column(Name = "last_error", StringLength = 512)]
    public string LastError { get; set; } = string.Empty;

    /// <summary>下次重试时间，做退避。</summary>
    [Column(Name = "next_retry_at")]
    public DateTime NextRetryAt { get; set; } = DateTime.UtcNow;

    /// <summary>平台 Id（租户）。</summary>
    [Column(Name = "platform_id")]
    public long PlatformId { get; set; }

    /// <summary>商户 Id（租户）。</summary>
    [Column(Name = "merchant_id")]
    public long MerchantId { get; set; }
}

/// <summary>库存动作。与幂等键里的 action 是同一个值。</summary>
public static class StockActions
{
    /// <summary>商品创建时初始化。</summary>
    public const string Init = "init";

    /// <summary>下单锁定：locked += q，available -= q。</summary>
    public const string Lock = "lock";

    /// <summary>支付成功扣减：locked -= q，deducted += q。</summary>
    public const string Deduct = "deduct";

    /// <summary>取消 / 超时关单释放：locked -= q，available += q。</summary>
    public const string Release = "release";

    /// <summary>退款通过回补：deducted -= q，available += q。</summary>
    public const string Replenish = "replenish";

    /// <summary>后台手工调整可用库存。</summary>
    public const string Adjust = "adjust";

    /// <summary>秒杀场次创建划出。</summary>
    public const string SeckillReserve = "seckill_reserve";

    /// <summary>秒杀场次结束 / 中止回补。</summary>
    public const string SeckillRelease = "seckill_release";
}

/// <summary>补偿记录的处理状态。</summary>
public static class PendingReleaseStatus
{
    /// <summary>待处理。</summary>
    public const int Pending = 0;

    /// <summary>已处理成功。</summary>
    public const int Done = 1;

    /// <summary>重试次数用尽，转人工。</summary>
    public const int Failed = 2;
}