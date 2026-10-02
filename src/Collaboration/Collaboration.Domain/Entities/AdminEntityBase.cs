using FreeSql.DataAnnotations;

namespace Collaboration.Domain.Entities;

/// <summary>
/// 后台业务实体基类：需要按平台 / 商户隔离，并记录谁操作的、何时操作。
/// </summary>
/// <remarks>
/// 适用实体：商品、订单、活动、商户、分类、券、秒杀场次、积分账户等所有后台业务数据。
/// 关键约束：创建人写入后永不修改；操作人每次更新被覆盖。
/// 依据：DATA_SPEC.md 2.2。
/// </remarks>
public abstract class AdminEntityBase : EntityBase
{
    /// <summary>
    /// 创建人 Id，雪花 Id。
    /// </summary>
    /// <remarks>
    /// 写入后永不修改。后台账号为 User.Id；系统任务为 0。
    /// </remarks>
    [Column(Name = "created_by_id")]
    public long CreatedById { get; set; }

    /// <summary>
    /// 创建人姓名快照。
    /// </summary>
    /// <remarks>
    /// 存快照而非联表，避免用户改名后历史记录跟着变。
    /// </remarks>
    [Column(Name = "created_by_name", StringLength = 64)]
    public string CreatedByName { get; set; } = string.Empty;

    /// <summary>
    /// 最后操作人 Id，雪花 Id。
    /// </summary>
    /// <remarks>
    /// 每次 Update 由 AOP 覆盖为当前操作人。
    /// </remarks>
    [Column(Name = "operation_id")]
    public long OperationId { get; set; }

    /// <summary>
    /// 最后操作人姓名快照。
    /// </summary>
    /// <remarks>
    /// 每次 Update 由 AOP 覆盖。
    /// </remarks>
    [Column(Name = "operation_name", StringLength = 64)]
    public string OperationName { get; set; } = string.Empty;

    /// <summary>
    /// 所属平台 Id，雪花 Id。
    /// </summary>
    /// <remarks>
    /// 0 表示平台自身 / 全局数据。租户 AOP 按此字段隔离。
    /// </remarks>
    [Column(Name = "platform_id")]
    public long PlatformId { get; set; }

    /// <summary>
    /// 所属商户 Id，雪花 Id。
    /// </summary>
    /// <remarks>
    /// 0 表示平台自身数据（如平台级活动、平台分类），不是「没有商户」。
    /// </remarks>
    [Column(Name = "merchant_id")]
    public long MerchantId { get; set; }
}

