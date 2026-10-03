using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace PointService.Domain.Entities;

/// <summary>积分账户。<b>一个客户一个账户</b>，跨平台共用同一个余额（BUSINESS.md 13.1）。</summary>
/// <remarks>
/// 两个计数与库存同构：available（可抵扣）/ frozen（下单冻结中）。
/// 冻结模型是必须的——下单时先把积分划走，支付成功才真正扣掉，
/// 取消或订单超时才能原路退回，而不是「扣了再退」。
/// </remarks>
[Table(Name = "point_account")]
public class PointAccount : EntityBase
{
    /// <summary>客户 Id。账户全局唯一，所以它就是这张表的业务主键。</summary>
    [Column(Name = "customer_id")]
    public long CustomerId { get; set; }

    /// <summary>可用积分（可下单抵扣）。</summary>
    [Column(Name = "available")]
    public long Available { get; set; }

    /// <summary>冻结积分（下单占用，未支付）。</summary>
    [Column(Name = "frozen")]
    public long Frozen { get; set; }

    /// <summary>累计发放，用于后台统计。</summary>
    [Column(Name = "total_earned")]
    public long TotalEarned { get; set; }

    /// <summary>累计消耗（含实扣与过期扣减）。</summary>
    [Column(Name = "total_used")]
    public long TotalUsed { get; set; }

    /// <summary>连续签到天数，断签清零，7 天一轮。</summary>
    [Column(Name = "sign_streak")]
    public int SignStreak { get; set; }

    /// <summary>上次签到日期（服务端本地日期 Asia/Shanghai）。用于判断当天是否已签、是否断签。</summary>
    [Column(Name = "sign_last_date")]
    public DateTime? SignLastDate { get; set; }
}

/// <summary>积分发放批次。发放时创建，365 天到期。</summary>
/// <remarks>
/// 之所以要批次而不是只记一个总余额，是因为 BUSINESS.md 13.5 要求
/// 「FIFO 先到期先用」，13.4 要求退款「回到**原冻结批次**、不重新计算有效期」。
/// 两条规则都必须知道积分「是哪一批来的」，只有一个余额数字是做不到的。
/// </remarks>
[Table(Name = "point_lot")]
public class PointLot : EntityBase
{
    /// <summary>客户 Id。</summary>
    [Column(Name = "customer_id")]
    public long CustomerId { get; set; }

    /// <summary>来源，见 <see cref="PointSources"/>。</summary>
    [Column(Name = "source", StringLength = 32)]
    public string Source { get; set; } = string.Empty;

    /// <summary>发放时的业务单号（注册赠送 / 订单号 / 签到日期等）。</summary>
    [Column(Name = "biz_no", StringLength = 64)]
    public string BizNo { get; set; } = string.Empty;

    /// <summary>本批发放总量。</summary>
    [Column(Name = "total")]
    public long Total { get; set; }

    /// <summary>本批还剩多少（已被冻结 / 消耗的部分不再算可用）。</summary>
    [Column(Name = "remaining")]
    public long Remaining { get; set; }

    /// <summary>到期时间，发放后 365 天（UTC）。</summary>
    [Column(Name = "expire_at")]
    public DateTime ExpireAt { get; set; }
}

/// <summary>冻结批次。记录「一笔业务冻结了多少积分」。</summary>
/// <remarks>
/// 有了它，取消 / 超时能原路解冻、退款能按比例回收，都不需要重新推算有效期。
/// </remarks>
[Table(Name = "point_lock")]
public class PointLock : EntityBase
{
    /// <summary>客户 Id。</summary>
    [Column(Name = "customer_id")]
    public long CustomerId { get; set; }

    /// <summary>业务单号（一般是订单号）。幂等键的一部分。</summary>
    [Column(Name = "biz_no", StringLength = 64)]
    public string BizNo { get; set; } = string.Empty;

    /// <summary>冻结数量。</summary>
    [Column(Name = "quantity")]
    public long Quantity { get; set; }

    /// <summary>状态，见 <see cref="PointLockStatus"/>。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = PointLockStatus.Frozen;
}

/// <summary>冻结明细：这笔冻结从哪些发放批次里各划走了多少。</summary>
[Table(Name = "point_lock_lot")]
public class PointLockLot
{
    /// <summary>主键。</summary>
    [Column(Name = "id", IsPrimary = true)]
    public long Id { get; set; }

    /// <summary>冻结批次 Id。</summary>
    [Column(Name = "lock_id")]
    public long LockId { get; set; }

    /// <summary>发放批次 Id。</summary>
    [Column(Name = "lot_id")]
    public long LotId { get; set; }

    /// <summary>从该发放批次划走的数量。</summary>
    [Column(Name = "quantity")]
    public long Quantity { get; set; }

    /// <summary>创建时间，UTC。</summary>
    [Column(Name = "created_at")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>积分流水。变动前后的余额冗余在这里，列表直接显示不必回表算。</summary>
[Table(Name = "point_record")]
public class PointRecord : EntityBase
{
    /// <summary>客户 Id。</summary>
    [Column(Name = "customer_id")]
    public long CustomerId { get; set; }

    /// <summary>业务单号。幂等键 = CustomerId + BizNo + Action。</summary>
    [Column(Name = "biz_no", StringLength = 64)]
    public string BizNo { get; set; } = string.Empty;

    /// <summary>动作，见 <see cref="PointActions"/>。</summary>
    [Column(Name = "action", StringLength = 16)]
    public string Action { get; set; } = string.Empty;

    /// <summary>变动数量。发放 / 冻结为正，实扣 / 过期为负。</summary>
    [Column(Name = "quantity")]
    public long Quantity { get; set; }

    /// <summary>变动前可用。</summary>
    [Column(Name = "before_available")]
    public long BeforeAvailable { get; set; }

    /// <summary>变动后可用。</summary>
    [Column(Name = "after_available")]
    public long AfterAvailable { get; set; }

    /// <summary>变动前冻结。</summary>
    [Column(Name = "before_frozen")]
    public long BeforeFrozen { get; set; }

    /// <summary>变动后冻结。</summary>
    [Column(Name = "after_frozen")]
    public long AfterFrozen { get; set; }

    /// <summary>关联发放批次的到期时间，方便流水直接显示「什么时候过期」。</summary>
    [Column(Name = "lot_expire_at")]
    public DateTime? LotExpireAt { get; set; }

    /// <summary>备注。</summary>
    [Column(Name = "remark", StringLength = 512)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>积分动作。与幂等键里的 action 是同一个值（BUSINESS.md 13.7）。</summary>
public static class PointActions
{
    /// <summary>发放（注册赠送 / 订单完成 / 首评 / 签到）。</summary>
    public const string Earn = "earn";

    /// <summary>下单冻结：available -= q，frozen += q。</summary>
    public const string Lock = "lock";

    /// <summary>取消 / 超时解冻：frozen -= q，available += q（退回原批次）。</summary>
    public const string Unfreeze = "unfreeze";

    /// <summary>支付成功实扣：frozen -= q。钱已经付出去了，这笔积分就此消失。</summary>
    public const string Consume = "consume";

    /// <summary>退款按比例回收：把当时抵扣的积分退一部分回来（按原冻结批次）。</summary>
    public const string Refund = "refund";

    /// <summary>过期扣减。</summary>
    public const string Expire = "expire";

    /// <summary>签到发放（独立 action，便于按 action 统计签到积分）。</summary>
    public const string SignIn = "signin";
}

/// <summary>积分发放来源。</summary>
public static class PointSources
{
    /// <summary>注册赠送，一次性 +100。</summary>
    public const string Register = "register";

    /// <summary>订单签收完成，实付每满 1.00 元 1 积分。</summary>
    public const string OrderCompleted = "order_completed";

    /// <summary>发表首评 +20。</summary>
    public const string FirstEvaluate = "first_evaluate";

    /// <summary>每日签到。</summary>
    public const string SignIn = "signin";
}

/// <summary>冻结批次状态。</summary>
public static class PointLockStatus
{
    /// <summary>冻结中。</summary>
    public const int Frozen = 0;

    /// <summary>已实扣（支付成功）。</summary>
    public const int Consumed = 1;

    /// <summary>已解冻（取消 / 超时）。</summary>
    public const int Unfrozen = 2;

    /// <summary>已按比例回收（退款）。</summary>
    public const int Refunded = 3;
}

/// <summary>积分业务常量。</summary>
public static class PointRules
{
    /// <summary>积分有效期：发放后 365 天（BUSINESS.md 13.5）。</summary>
    public const int ValidDays = 365;

    /// <summary>单客户余额上限，超出部分截断不入账（BUSINESS.md 13.7）。</summary>
    public const long BalanceCap = 100_000;

    /// <summary>注册赠送积分（一次性）。</summary>
    public const long RegisterGift = 100;

    /// <summary>发表首评赠送积分。</summary>
    public const long FirstEvaluateGift = 20;

    /// <summary>抵扣汇率：100 积分 = 1.00 元。</summary>
    public const long PointsPerYuan = 100;

    /// <summary>
    /// 获取积分：实付每满 1.00 元 1 积分，<b>向下取整</b>（BUSINESS.md 13.6）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="PointsPerYuan"/> 是<b>反向</b>的两条汇率，别混：
    /// 一条是「花多少积分抵 1 元」，一条是「花 1 元给多少积分」。
    /// 写成 100 的话，51 元订单会发 5100 积分，抵回来等于白送——
    /// 用户下单 100 元、抵扣 100 元、再送 100 元，积分就成了永动机。
    /// </remarks>
    public const long PointsPerYuanPerYuan = 1;

    /// <summary>连续签到 7 天一轮的奖励。</summary>
    public static readonly long[] SignInRewards = [1, 2, 3, 5, 8, 10, 15];
}