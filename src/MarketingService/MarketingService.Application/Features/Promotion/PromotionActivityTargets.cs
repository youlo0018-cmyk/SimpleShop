using MarketingService.Application.Services;
using MarketingService.Domain.Entities;
using MarketingService.Domain.Services;

namespace MarketingService.Application.Features.Promotion;

/// <summary>
/// 活动适用目标（<c>Targets</c>）的存在性与归属校验（DATA_SPEC 5.32）。
/// </summary>
/// <remarks>
/// <para>抽成一处是因为新建与编辑是同一个表单的两条路径，规则必须一模一样
/// （这个项目已经栽过一次：编辑路径少写一条规则，于是只有编辑能造出非法配置）。</para>
///
/// <para><b>只校验存在与归属</b>，不要求审核通过 / 已上架：运营常常先配好活动、
/// 等商品上架后自动生效（规格 16.4 明确要求不要卡它们）。</para>
/// </remarks>
internal static class PromotionActivityTargets
{
    /// <summary>错误提示里最多列几个目标。</summary>
    private const int MaxListedTargets = 5;

    /// <summary>校验目标，通过返回 null。</summary>
    /// <param name="targetType">适用范围类型，见 <see cref="TargetTypes"/>。</param>
    /// <param name="targets">目标 JSON 文本。</param>
    /// <param name="platformId">活动所属平台。</param>
    /// <param name="merchantId">活动所属商户，0 表示平台级活动。</param>
    /// <param name="products">商品服务端口。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>不通过时的中文原因。</returns>
    public static async Task<string?> ValidateAsync(
        int targetType,
        string? targets,
        long platformId,
        long merchantId,
        IProductPort products,
        CancellationToken ct)
    {
        // 全场活动没有目标可校验。但**商户级**活动暂时不允许全场：
        // 优惠引擎目前不带商户维度（活动只按平台 + 目标匹配），
        // 一条商户级的全场活动会作用到同平台所有商户的商品上 —— 那是跨商户改价。
        // 在引擎补上商户维度之前，这里先挡住；补上之后这条限制就可以撤掉。
        if (targetType == TargetTypes.All)
        {
            return merchantId > 0
                ? "商户活动不能设为全场，请指定商品或规格（本店全场活动待优惠引擎支持商户维度后开放）"
                : null;
        }

        // ParseTargets 返回 HashSet；显式声明成接口类型，下面三元分支才能与 long[] 统一
        IReadOnlyCollection<long> ids = PromotionCalculator.ParseTargets(targets);

        // 理论上校验器已经拦过（指定范围必须填列表），这里再挡一次：
        // 「对谁都生效不了的活动」不值得依赖上游是否记得校验
        if (ids.Count == 0)
        {
            return targetType == TargetTypes.BySpu
                ? "指定商品范围时必须选择至少一个商品"
                : "指定规格范围时必须选择至少一个规格";
        }

        // 平台级活动（merchantId = 0）不做归属校验：平台账号本来就能管全平台的商品，
        // 而且优惠引擎的匹配是「平台 + 目标」，指到别的平台商品也不会作用于本平台订单。
        // 真正需要挡的是商户级活动 —— 那是跨租户改价。
        if (merchantId <= 0) return null;

        var spuIds = targetType == TargetTypes.BySpu ? ids : Array.Empty<long>();
        var skuIds = targetType == TargetTypes.BySku ? ids : Array.Empty<long>();

        var rejected = await products
            .CheckActivityTargetsAsync(spuIds, skuIds, platformId, merchantId, ct)
            .ConfigureAwait(false);

        // null = 商品服务不可用。必须拒绝：查不到 ≠ 没问题，
        // 放行的话就等于「商品服务一挂，适用范围随便填」。
        if (rejected is null)
        {
            return "商品服务暂时不可用，无法校验适用范围，请稍后重试";
        }

        if (rejected.Count == 0) return null;

        var details = string.Join("；", rejected.Take(MaxListedTargets).Select(a =>
            $"{(a.TargetType == TargetTypes.BySpu ? "商品" : "规格")} {a.TargetId}：{a.Reason}"));

        var more = rejected.Count > MaxListedTargets ? $"（共 {rejected.Count} 个不可用）" : string.Empty;

        return $"适用范围里有不可用的目标：{details}{more}";
    }
}
