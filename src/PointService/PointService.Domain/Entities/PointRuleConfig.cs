using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace PointService.Domain.Entities;

/// <summary>积分规则覆盖配置（权限点 <c>point:rule-update</c>）。</summary>
/// <remarks>
/// <b>这是一张「覆盖表」而不是「规则表」</b>：规则永远存在于代码常量
/// <see cref="PointRules"/> 里，只有被后台改过的才落到这张表。
///
/// <para>为什么不用「每次发放都查这张表」：积分发放在下单主链路上是同步调用，
/// 多一次配置查询就多一个抖动点；而且规则只有 7 条，
/// 做成「查不到就用默认值」既省掉了那次查询，又让单元测试在没有这张表数据时
/// 仍然跑出与规格完全一致的结果。</para>
///
/// <para>继承 <see cref="EntityBase"/>（不带租户字段）而不是 <c>AdminEntityBase</c>：
/// 积分账户是**全局唯一**的（BUSINESS.md 13.1），规则自然也是全局一份，
/// 不按平台分开。</para>
/// </remarks>
[Table(Name = "point_rule_config")]
public class PointRuleConfig : EntityBase
{
    /// <summary>规则键，见 <see cref="PointRuleKeys"/>。</summary>
    [Column(Name = "rule_key", StringLength = 64)]
    public string RuleKey { get; set; } = string.Empty;

    /// <summary>规则值，文本存储。签到奖励存 JSON 数组，如 <c>[1,2,3,5,8,10,15]</c>。</summary>
    [Column(Name = "rule_value", StringLength = 512)]
    public string RuleValue { get; set; } = string.Empty;

    /// <summary>规则说明，展示给运营看「这一条改的是什么」。</summary>
    [Column(Name = "description", StringLength = 256)]
    public string Description { get; set; } = string.Empty;

    /// <summary>最后修改人 Id。</summary>
    [Column(Name = "updated_by_id")]
    public long UpdatedById { get; set; }

    /// <summary>最后修改人姓名。</summary>
    [Column(Name = "updated_by_name", StringLength = 64)]
    public string UpdatedByName { get; set; } = string.Empty;
}

/// <summary>可配置的积分规则键。</summary>
public static class PointRuleKeys
{
    /// <summary>单客户余额上限。</summary>
    public const string BalanceCap = "balance_cap";

    /// <summary>积分有效期（天）。</summary>
    public const string ValidDays = "valid_days";

    /// <summary>注册赠送积分。</summary>
    public const string RegisterGift = "register_gift";

    /// <summary>发表首评赠送积分。</summary>
    public const string FirstEvaluateGift = "first_evaluate_gift";

    /// <summary>抵扣汇率：多少积分抵 1.00 元。</summary>
    public const string PointsPerYuan = "points_per_yuan";

    /// <summary>获取汇率：实付 1.00 元给多少积分（向下取整）。</summary>
    public const string EarnPointsPerYuan = "earn_points_per_yuan";

    /// <summary>签到连续奖励，JSON 整数数组。</summary>
    public const string SignInRewards = "sign_in_rewards";
}
