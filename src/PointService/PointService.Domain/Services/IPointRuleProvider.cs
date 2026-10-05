using PointService.Domain.Entities;

namespace PointService.Domain.Services;

/// <summary>积分规则读取端口。</summary>
/// <remarks>
/// <para><b>为什么要抽成端口</b>：规则本来是 <see cref="PointRules"/> 里的常量，
/// 现在后台能改了。仓储与处理器就不该再直接读常量——那样规则改了也不生效，
/// 而「配置存了但不生效」是最难排查的一类问题（后台显示保存成功，实际行为没变）。</para>
///
/// <para>实现内部带缓存：积分发放在下单主链路上，不能每次都查配置表。</para>
/// </remarks>
public interface IPointRuleProvider
{
    /// <summary>取当前生效的规则快照。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>规则快照。未被覆盖的项自动回落到代码默认值。</returns>
    Task<PointRuleSnapshot> GetAsync(CancellationToken ct = default);

    /// <summary>强制下次读取时重新加载规则。</summary>
    /// <remarks>
    /// <b>后台保存规则后必须调用</b>：不调用的话运营会看到「保存成功，但要等 30 秒才生效」——
    /// 那正是「配置存了但不生效」，是最难排查的一类问题。
    ///
    /// <para>方法声明在接口上而不是只留在实现类上，是为了让应用层能调用它
    /// 而不必引用 Infrastructure（分层是 Domain ← Application ← Infrastructure ← Api）。
    /// 早先的写法是在处理器里注入具体实现类，那会让应用层反向依赖基础设施。</para>
    /// </remarks>
    void Invalidate();
}

/// <summary>积分规则快照。</summary>
/// <param name="BalanceCap">单客户余额上限，超出截断不入账。</param>
/// <param name="ValidDays">积分有效期（天）。</param>
/// <param name="RegisterGift">注册赠送积分。</param>
/// <param name="FirstEvaluateGift">发表首评赠送积分。</param>
/// <param name="PointsPerYuan">抵扣汇率：多少积分抵 1.00 元。</param>
/// <param name="EarnPointsPerYuan">获取汇率：实付 1.00 元给多少积分（向下取整）。</param>
/// <param name="SignInRewards">签到连续奖励，第 8 天回到第 1 档。</param>
public sealed record PointRuleSnapshot(
    long BalanceCap,
    int ValidDays,
    long RegisterGift,
    long FirstEvaluateGift,
    long PointsPerYuan,
    long EarnPointsPerYuan,
    IReadOnlyList<long> SignInRewards)
{
    /// <summary>按「未被后台覆盖」的规格默认值构造快照。</summary>
    /// <returns>与 BUSINESS.md 13.2/13.3/13.5/13.6/13.7 完全一致的默认规则。</returns>
    public static PointRuleSnapshot Defaults() => new(
        PointRules.BalanceCap,
        PointRules.ValidDays,
        PointRules.RegisterGift,
        PointRules.FirstEvaluateGift,
        PointRules.PointsPerYuan,
        PointRules.PointsPerYuanPerYuan,
        PointRules.SignInRewards);
}
