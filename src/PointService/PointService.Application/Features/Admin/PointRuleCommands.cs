using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace PointService.Application.Features.Admin;

/// <summary>读取积分规则维护页的当前规则。</summary>
public record QueryPointRulesCommand : IRequest<ApiResponse<PointRulesView>>;

/// <summary>保存积分规则（覆盖值）。</summary>
/// <param name="BalanceCap">单客户余额上限。</param>
/// <param name="ValidDays">积分有效期（天）。</param>
/// <param name="RegisterGift">注册赠送积分。</param>
/// <param name="FirstEvaluateGift">发表首评赠送积分。</param>
/// <param name="PointsPerYuan">抵扣汇率：多少积分抵 1.00 元。</param>
/// <param name="EarnPointsPerYuan">获取汇率：实付 1.00 元给多少积分（向下取整）。</param>
/// <param name="SignInRewards">签到连续奖励，整数数组，如 1,2,3,5,8,10,15。</param>
public record SavePointRulesCommand(
    long BalanceCap,
    int ValidDays,
    long RegisterGift,
    long FirstEvaluateGift,
    long PointsPerYuan,
    long EarnPointsPerYuan,
    IReadOnlyList<long> SignInRewards) : IRequest<ApiResponse>;

/// <summary>积分规则维护页视图。</summary>
/// <param name="BalanceCap">单客户余额上限。</param>
/// <param name="ValidDays">积分有效期（天）。</param>
/// <param name="RegisterGift">注册赠送积分。</param>
/// <param name="FirstEvaluateGift">发表首评赠送积分。</param>
/// <param name="PointsPerYuan">抵扣汇率：多少积分抵 1.00 元。</param>
/// <param name="EarnPointsPerYuan">获取汇率：实付 1.00 元给多少积分。</param>
/// <param name="SignInRewards">签到连续奖励。</param>
/// <param name="BalanceCapDefault">余额上限的规格默认值，界面用它提示「恢复默认」。</param>
/// <param name="ValidDaysDefault">有效期的规格默认值。</param>
/// <param name="RegisterGiftDefault">注册赠送的规格默认值。</param>
/// <param name="FirstEvaluateGiftDefault">首评赠送的规格默认值。</param>
/// <param name="PointsPerYuanDefault">抵扣汇率的规格默认值。</param>
/// <param name="EarnPointsPerYuanDefault">获取汇率的规格默认值。</param>
/// <param name="SignInRewardsDefault">签到奖励的规格默认值。</param>
/// <param name="IsCustomized">是否有任何一条规则被后台改过（false 表示全是默认值）。</param>
public sealed record PointRulesView(
    long BalanceCap, int ValidDays, long RegisterGift, long FirstEvaluateGift,
    long PointsPerYuan, long EarnPointsPerYuan, IReadOnlyList<long> SignInRewards,
    long BalanceCapDefault, int ValidDaysDefault, long RegisterGiftDefault,
    long FirstEvaluateGiftDefault, long PointsPerYuanDefault, long EarnPointsPerYuanDefault,
    IReadOnlyList<long> SignInRewardsDefault, bool IsCustomized);

/// <summary>积分规则命令的校验器注册。</summary>
public static class PointRuleValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    /// <remarks>校验器写在嵌套静态类里，AddValidatorsFromAssembly 扫不到，必须显式注册。</remarks>
    public static void AddPointRuleValidators(IServiceCollection services)
        => services.AddScoped<IValidator<SavePointRulesCommand>, SavePointRulesValidator>();

    /// <summary>保存积分规则校验。</summary>
    /// <remarks>
    /// <b>上下界刻意比「代码默认值」宽得多</b>：规则本来就允许后台调，
    /// 校验只拦「明显不合法」的（负数、有效期 0 天、签到奖励为空或含负数），
    /// 而不是写死成「必须等于 100000」—— 那会让「恢复默认」变成唯一能通过的操作。
    /// </remarks>
    private sealed class SavePointRulesValidator : AbstractValidator<SavePointRulesCommand>
    {
        /// <summary>构造校验器。</summary>
        public SavePointRulesValidator()
        {
            RuleFor(x => x.BalanceCap).InclusiveBetween(1, 100_000_000).WithMessage("余额上限不合法");
            RuleFor(x => x.ValidDays).InclusiveBetween(1, 36500).WithMessage("积分有效期必须在 1 ~ 36500 天之间");
            RuleFor(x => x.RegisterGift).InclusiveBetween(0, 1_000_000).WithMessage("注册赠送积分不合法");
            RuleFor(x => x.FirstEvaluateGift).InclusiveBetween(0, 1_000_000).WithMessage("首评赠送积分不合法");
            RuleFor(x => x.PointsPerYuan).InclusiveBetween(1, 1_000_000).WithMessage("抵扣汇率必须大于 0");
            RuleFor(x => x.EarnPointsPerYuan).InclusiveBetween(0, 1_000_000).WithMessage("获取汇率不合法");

            RuleFor(x => x.SignInRewards)
                // CascadeMode.Stop：默认级联是 Continue，NotNull 失败后 Must 照样执行。
                // 这里两个 Must 都自带 null 判断，所以不会抛异常，但加了级联之后
                // 「没填」就只回一条「请填写签到连续奖励」，而不是同时回三条错误。
                .Cascade(CascadeMode.Stop)
                .NotNull().WithMessage("请填写签到连续奖励")
                .Must(r => r is not null && r.Count is > 0 and <= 31)
                .WithMessage("签到奖励档位数量必须在 1 ~ 31 之间")
                .Must(r => r is null || r.All(x => x >= 0)).WithMessage("签到奖励不能为负数");
        }
    }
}
