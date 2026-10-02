using FreeSql.DataAnnotations;

namespace Collaboration.Domain.Entities;

/// <summary>
/// 前台客户私有数据基类：归属某个客户。
/// </summary>
/// <remarks>
/// 适用实体：收货地址、收藏、积分账户、客户档案。
/// 关键约束：操作人就是 CustomerId 本身，不另设操作人字段。
/// 不与 AdminEntityBase 同时继承——一张表要么按租户隔离，要么按客户隔离。
/// 依据：DATA_SPEC.md 2.3。
/// </remarks>
public abstract class CustomerEntityBase : EntityBase
{
    /// <summary>
    /// 归属客户 Id，雪花 Id。
    /// </summary>
    /// <remarks>
    /// 客户 AOP 按此字段自动过滤，客户只能看到自己的数据。
    /// </remarks>
    [Column(Name = "customer_id")]
    public long CustomerId { get; set; }

    /// <summary>
    /// 客户用户名快照。
    /// </summary>
    /// <remarks>
    /// 昵称可随时修改，历史记录不应跟着变。
    /// 手机号**不放基类**——订单有收货信息快照，地址簿另有专表。
    /// </remarks>
    [Column(Name = "customer_name", StringLength = 64)]
    public string CustomerName { get; set; } = string.Empty;
}

