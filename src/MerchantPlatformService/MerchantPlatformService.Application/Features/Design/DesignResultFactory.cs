using MerchantPlatformService.Domain.Services;

namespace MerchantPlatformService.Application.Features.Design;

/// <summary>装修读取结果的装配。</summary>
internal static class DesignResultFactory
{
    /// <summary>从未配置过时返回的空结果（平台：首页 + 我的页）。</summary>
    /// <returns>空配置。</returns>
    /// <remarks>
    /// 返回<b>结构完整但内容为空</b>的配置，而不是空串：
    /// 后台搭建器拿到空串会按「配置损坏」处理，而「还没装修过」是完全正常的初始状态。
    /// </remarks>
    public static DesignResult Empty() => new(BuildDefault(DesignPages.PlatformPages), 0, false, []);

    /// <summary>从未配置过时返回的空结果（商户：只有店铺页）。</summary>
    /// <returns>空配置。</returns>
    public static DesignResult EmptyMerchant() => new(BuildDefault(DesignPages.MerchantPages), 0, false, []);

    /// <summary>按「草稿优先」装配读取结果。</summary>
    /// <param name="draftJson">草稿 JSON。</param>
    /// <param name="publishedJson">已发布 JSON。</param>
    /// <param name="version">已发布版本号。</param>
    /// <returns>装修配置。</returns>
    public static DesignResult FromDraft(string draftJson, string publishedJson, int version)
    {
        var hasDraft = !string.IsNullOrWhiteSpace(draftJson);
        // 有草稿就返回草稿：运营在后台拖的组件必须立刻能看到，
        // 否则会出现「我明明改了，页面没变」
        var json = hasDraft ? draftJson : publishedJson;
        return new DesignResult(json, version, hasDraft, []);
    }

    /// <summary>按「草稿优先」装配读取结果，并把本次保存的告警一并带回。</summary>
    /// <param name="draftJson">草稿 JSON。</param>
    /// <param name="publishedJson">已发布 JSON。</param>
    /// <param name="version">已发布版本号。</param>
    /// <param name="warnings">本次保存产生的告警（如配色被剔除）。</param>
    /// <returns>装修配置。</returns>
    /// <remarks>
    /// 保存接口必须把告警**原样回传**。日志里记了不算完 ——
    /// 搭建器要靠它告诉运营「你传的配色没生效」，否则运营只会看到自己设的红色
    /// 变成了平台色，却不知道是自己没权限改。
    /// </remarks>
    public static DesignResult FromDraft(
        string draftJson, string publishedJson, int version, IReadOnlyList<string> warnings)
    {
        var result = FromDraft(draftJson, publishedJson, version);
        return result with { Warnings = warnings };
    }

    /// <summary>小程序只读已发布配置，不暴露草稿。</summary>
    /// <param name="publishedJson">已发布 JSON。</param>
    /// <param name="version">已发布版本号。</param>
    /// <returns>装修配置；从未发布时返回结构完整的空店铺配置。</returns>
    public static DesignResult FromPublished(string? publishedJson, int version)
    {
        if (string.IsNullOrWhiteSpace(publishedJson))
        {
            var empty = EmptyMerchant();
            return empty with { Version = version };
        }

        return new DesignResult(publishedJson, version, false, []);
    }

    /// <summary>构造一份结构完整的空配置。</summary>
    /// <param name="pages">要建的页面。</param>
    /// <returns>配置 JSON。</returns>
    private static string BuildDefault(string[] pages)
    {
        var config = new DesignConfig();
        foreach (var page in pages)
        {
            config.Pages[page] = new DesignPage();
        }
        return config.ToJson();
    }
}
