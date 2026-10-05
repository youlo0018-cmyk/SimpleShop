using System.Text.Json;
using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using Collaboration.Domain.Infrastructure;
using FreeSql;
using MediatR;
using PointService.Domain.Entities;
using PointService.Domain.Services;

namespace PointService.Application.Features.Admin;

/// <summary>读取积分规则处理器。</summary>
public sealed class QueryPointRulesHandler
    : IRequestHandler<QueryPointRulesCommand, ApiResponse<PointRulesView>>
{
    private readonly IPointRuleProvider _rules;
    private readonly IFreeSql _db;

    /// <summary>构造处理器。</summary>
    /// <param name="rules">积分规则提供器。</param>
    /// <param name="db">FreeSql 实例，用于判断哪些规则被后台改过。</param>
    public QueryPointRulesHandler(IPointRuleProvider rules, IFreeSql db)
    {
        _rules = rules;
        _db = db;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 一次返回「当前值 + 规格默认值」两组数据：界面靠 <c>IsCustomized</c> 决定要不要显示
    /// 「恢复默认」按钮，靠两组的差异决定哪一行要标「已修改」。
    /// 只返回当前值的话，运营改完之后就再也看不出这条规则原本是多少了。
    /// </remarks>
    public async Task<ApiResponse<PointRulesView>> Handle(QueryPointRulesCommand request, CancellationToken ct)
    {
        var current = await _rules.GetAsync(ct).ConfigureAwait(false);
        var d = PointRuleSnapshot.Defaults();

        var customized = await _db.Select<PointRuleConfig>()
            .Where(a => a.RuleValue != null && a.RuleValue != string.Empty)
            .CountAsync(ct)
            .ConfigureAwait(false) > 0;

        var view = new PointRulesView(
            current.BalanceCap, current.ValidDays, current.RegisterGift, current.FirstEvaluateGift,
            current.PointsPerYuan, current.EarnPointsPerYuan, current.SignInRewards,
            d.BalanceCap, d.ValidDays, d.RegisterGift, d.FirstEvaluateGift,
            d.PointsPerYuan, d.EarnPointsPerYuan, d.SignInRewards,
            customized);

        return ApiResults.Ok(view);
    }
}

/// <summary>保存积分规则处理器。</summary>
public sealed class SavePointRulesHandler : IRequestHandler<SavePointRulesCommand, ApiResponse>
{
    private readonly IFreeSql _db;
    private readonly IPointRuleProvider _rules;

    /// <summary>构造处理器。</summary>
    /// <param name="db">FreeSql 实例。</param>
    /// <param name="rules">规则提供器，保存后要失效缓存。</param>
    public SavePointRulesHandler(IFreeSql db, IPointRuleProvider rules)
    {
        _db = db;
        _rules = rules;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>整组覆盖</b>而不是「增量改某一条」：界面是「积分规则维护」一整页，
    /// 保存时提交全部 7 条。写成增量的话，前端漏传一条就会静默保留旧值，
    /// 而运营以为自己已经改过了。
    ///
    /// <para>🔴 <b>不追溯已发放的积分</b>：改了有效期或余额上限，
    /// 只对<strong>之后</strong>的发放生效，已发放的批次保持原有到期时间。
    /// 追溯修改意味着要批量改写历史批次——那会让「我的积分」页面上的到期时间集体跳变，
    /// 而用户手里的积分是「已承诺」的，追溯变更等于单方面改合同。</para>
    /// </remarks>
    public async Task<ApiResponse> Handle(SavePointRulesCommand request, CancellationToken ct)
    {
        var ctx = TenantContextHolder.Current;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PointRuleKeys.BalanceCap] = request.BalanceCap.ToString(),
            [PointRuleKeys.ValidDays] = request.ValidDays.ToString(),
            [PointRuleKeys.RegisterGift] = request.RegisterGift.ToString(),
            [PointRuleKeys.FirstEvaluateGift] = request.FirstEvaluateGift.ToString(),
            [PointRuleKeys.PointsPerYuan] = request.PointsPerYuan.ToString(),
            [PointRuleKeys.EarnPointsPerYuan] = request.EarnPointsPerYuan.ToString(),
            [PointRuleKeys.SignInRewards] = JsonSerializer.Serialize(request.SignInRewards)
        };

        var now = DateTime.UtcNow;

        foreach (var (key, value) in values)
        {
            var existing = await _db.Select<PointRuleConfig>()
                .Where(a => a.RuleKey == key)
                .FirstAsync(ct)
                .ConfigureAwait(false);

            if (existing is null)
            {
                await _db.Insert(new PointRuleConfig
                {
                    Id = SnowflakeId.NewId(),
                    CreatedAt = now,
                    RuleKey = key,
                    RuleValue = value,
                    Description = Describe(key),
                    UpdatedById = ctx.UserId,
                    UpdatedByName = ctx.UserName
                }).ExecuteAffrowsAsync(ct).ConfigureAwait(false);

                continue;
            }

            // 用 Where + Set 而不是 Update(entity)：
            // 后者在雪花主键（IsIdentity=false）下会生成空 SET 子句、一条 SQL 都不发
            // 就返回 0，接口回「成功」而数据纹丝不动（见 CrudRepository 的 P0 修复说明）。
            await _db.Update<PointRuleConfig>()
                .Where(a => a.Id == existing.Id)
                .Set(a => new PointRuleConfig
                {
                    RuleValue = value,
                    Description = Describe(key),
                    UpdatedAt = now,
                    UpdatedById = ctx.UserId,
                    UpdatedByName = ctx.UserName
                })
                .ExecuteAffrowsAsync(ct)
                .ConfigureAwait(false);
        }

        _rules.Invalidate();

        return ApiResponseFactory.Ok("积分规则已保存，对之后的发放生效");
    }

    /// <summary>规则的中文说明，随配置一起存，便于后台直接展示「这一条改的是什么」。</summary>
    /// <param name="key">规则键。</param>
    /// <returns>中文说明；未知键返回空串。</returns>
    private static string Describe(string key) => key switch
    {
        PointRuleKeys.BalanceCap => "单客户余额上限，超出部分截断不入账",
        PointRuleKeys.ValidDays => "积分有效期（发放后多少天到期）",
        PointRuleKeys.RegisterGift => "新客户注册赠送的积分",
        PointRuleKeys.FirstEvaluateGift => "客户发表首评赠送的积分",
        PointRuleKeys.PointsPerYuan => "抵扣汇率：多少积分抵 1.00 元",
        PointRuleKeys.EarnPointsPerYuan => "获取汇率：实付 1.00 元给多少积分（向下取整）",
        PointRuleKeys.SignInRewards => "每日签到的连续奖励档位，第 N+1 天回到第 1 档",
        _ => string.Empty
    };
}
