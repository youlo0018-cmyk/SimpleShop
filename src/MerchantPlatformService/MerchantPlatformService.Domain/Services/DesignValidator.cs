using System.Text.Json;

namespace MerchantPlatformService.Domain.Services;

/// <summary>装修配置校验结果。</summary>
/// <param name="Errors">阻断性错误，非空即<b>整单保存失败</b>。</param>
/// <param name="Warnings">提醒性告警，保存成功但运营需要知道。</param>
/// <param name="ProductIds">配置里手动指定、待做可见性校验的商品 Id。</param>
public sealed record DesignValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<long> ProductIds)
{
    /// <summary>是否通过。</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>装修配置校验器（纯逻辑，可单元测试）。</summary>
public static class DesignValidator
{
    /// <summary>组件实例 id 的最大长度。</summary>
    public const int MaxComponentIdLength = 32;

    /// <summary>TabBar 最少项数。</summary>
    public const int MinTabBarItems = 2;

    /// <summary>TabBar 最多项数。</summary>
    public const int MaxTabBarItems = 5;

    /// <summary>商户装修里要<b>剔除</b>的配色键。</summary>
    /// <remarks>
    /// 商户不能改任何配色（用户明确要求「商户只允许调组件顺序，不允许换配色」）。
    /// 做法是<b>剔除并告警</b>而不是报错：后台属性面板根本不提供颜色选择器，
    /// 能传进来的只可能是运营手改请求体或旧版本残留。直接拒绝会让整个店铺装修存不进去，
    /// 而剔除掉正好达到目的——颜色继承平台。
    /// </remarks>
    public static readonly string[] ColorKeys =
        ["color", "colorText", "colorSub", "bgColor", "textColor", "primaryColor"];

    /// <summary>校验装修配置。</summary>
    /// <param name="config">配置。</param>
    /// <param name="forMerchant">true 表示按商户装修规则校验。</param>
    /// <returns>校验结果。</returns>
    public static DesignValidationResult Validate(DesignConfig config, bool forMerchant)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var products = new List<long>();

        ValidateTheme(config.Theme, errors);

        // 商户装修的主题三档色同样要剔除：只剔组件里的 props 不够，
        // 商户完全可以把 primary/tabColor/background 换掉，
        // 而那正是「颜色继承平台」这条规则最显眼的三处。
        // 顺序在 ValidateTheme 之后 —— 先确认它是个合法颜色再剔除，
        // 否则传个乱码进来会被当成「成功剔除」，运营永远不知道自己填错了。
        if (forMerchant) StripTheme(config.Theme, warnings);

        ValidateTabBar(config.TabBar, errors);
        ValidatePages(config, forMerchant, errors, warnings, products);

        return new DesignValidationResult(errors, warnings, products.Distinct().ToList());
    }

    /// <summary>校验三档主题色。</summary>
    /// <param name="theme">主题。</param>
    /// <param name="errors">错误收集。</param>
    private static void ValidateTheme(DesignTheme theme, List<string> errors)
    {
        CheckColor(theme.Primary, "主题色", errors);
        CheckColor(theme.TabColor, "TabBar 选中色", errors);
        CheckColor(theme.Background, "背景色", errors);
    }

    /// <summary>校验单个颜色值。</summary>
    /// <param name="color">颜色值；空串表示不覆盖默认。</param>
    /// <param name="label">字段中文名。</param>
    /// <param name="errors">错误收集。</param>
    private static void CheckColor(string color, string label, List<string> errors)
    {
        var error = MerchantRules.ValidateColor(color);
        if (error is not null) errors.Add($"{label}{error}");
    }

    /// <summary>校验 TabBar。空表示不配置底部导航，放行。</summary>
    /// <param name="tabBar">TabBar 项。</param>
    /// <param name="errors">错误收集。</param>
    private static void ValidateTabBar(List<DesignTabItem> tabBar, List<string> errors)
    {
        if (tabBar.Count == 0) return;

        if (tabBar.Count is < MinTabBarItems or > MaxTabBarItems)
        {
            errors.Add($"底部导航至少 {MinTabBarItems} 项、最多 {MaxTabBarItems} 项，当前 {tabBar.Count} 项");
        }

        foreach (var duplicate in tabBar
            .Where(a => !string.IsNullOrWhiteSpace(a.PagePath))
            .GroupBy(a => a.PagePath, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key))
        {
            errors.Add($"底部导航的页面路径重复：{duplicate}");
        }

        if (tabBar.Any(a => string.IsNullOrWhiteSpace(a.PagePath)))
        {
            errors.Add("底部导航的页面路径不能为空");
        }
    }

    /// <summary>校验各页画布。</summary>
    /// <param name="config">配置。</param>
    /// <param name="forMerchant">是否按商户装修校验。</param>
    /// <param name="errors">错误收集。</param>
    /// <param name="warnings">告警收集。</param>
    /// <param name="products">手动指定的商品 Id 收集。</param>
    private static void ValidatePages(
        DesignConfig config,
        bool forMerchant,
        List<string> errors,
        List<string> warnings,
        List<long> products)
    {
        var allowedPages = forMerchant ? DesignPages.MerchantPages : DesignPages.PlatformPages;

        foreach (var page in allowedPages)
        {
            if (!config.Pages.TryGetValue(page, out var canvas) || canvas is null)
            {
                errors.Add($"缺少「{page}」页面画布");
                continue;
            }

            ValidateComponents(page, canvas, forMerchant, errors, warnings, products);
        }

        // 商户画布锁定为店铺页：传了首页 / 我的页等于越权去改平台的东西
        if (!forMerchant) return;

        foreach (var page in config.Pages.Keys.Where(
            a => !DesignPages.MerchantPages.Contains(a, StringComparer.Ordinal)))
        {
            errors.Add($"商户装修不能配置「{page}」页面，只能配置店铺页");
        }
    }

    /// <summary>校验一页的组件列表。</summary>
    /// <param name="page">页面标识。</param>
    /// <param name="canvas">画布。</param>
    /// <param name="forMerchant">是否按商户装修校验。</param>
    /// <param name="errors">错误收集。</param>
    /// <param name="warnings">告警收集。</param>
    /// <param name="products">手动指定的商品 Id 收集。</param>
    private static void ValidateComponents(
        string page,
        DesignPage canvas,
        bool forMerchant,
        List<string> errors,
        List<string> warnings,
        List<long> products)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var component in canvas.Components)
        {
            if (string.IsNullOrWhiteSpace(component.Id) || component.Id.Length > MaxComponentIdLength)
            {
                errors.Add($"组件标识必须是 1 ~ {MaxComponentIdLength} 个字符");
                continue;
            }

            if (!ids.Add(component.Id))
            {
                errors.Add($"组件标识在「{page}」页内重复：{component.Id}");
                continue;
            }

            ValidateComponentType(component.Type, page, forMerchant, errors);
            ValidateSpanAndHeight(component, errors);
            CollectManualProducts(component, products, errors);

            if (forMerchant) StripColors(component, warnings);
        }
    }

    /// <summary>校验组件类型在该页面是否可用。</summary>
    /// <param name="type">组件类型。</param>
    /// <param name="page">页面标识。</param>
    /// <param name="forMerchant">是否按商户装修校验。</param>
    /// <param name="errors">错误收集。</param>
    private static void ValidateComponentType(
        string type, string page, bool forMerchant, List<string> errors)
    {
        var allowed = forMerchant
            ? DesignComponentRegistry.IsAllowedForMerchant(type)
            : DesignComponentRegistry.IsAllowedOnPlatformPage(type, page);

        if (allowed) return;

        errors.Add(forMerchant
            ? $"商户装修不能使用「{type}」组件"
            : $"「{page}」页不能使用「{type}」组件");
    }

    /// <summary>校验栅格列数与高度。</summary>
    /// <param name="component">组件。</param>
    /// <param name="errors">错误收集。</param>
    private static void ValidateSpanAndHeight(DesignComponent component, List<string> errors)
    {
        if (!DesignComponentRegistry.AllowedSpans.Contains(component.Span))
        {
            errors.Add($"组件宽度 {component.Span} 列不合法，只能是 1/2/3/4/6/12");
        }

        // 高度 0 表示「按组件默认」，不是非法值
        if (component.Height == 0) return;

        if (component.Height is < DesignComponentRegistry.MinHeight
            or > DesignComponentRegistry.MaxHeight)
        {
            errors.Add(
                $"组件高度 {component.Height} 超出 {DesignComponentRegistry.MinHeight} ~ " +
                $"{DesignComponentRegistry.MaxHeight} 范围");
        }
    }

    /// <summary>剔除商户装修里的配色字段并告警。</summary>
    /// <param name="component">组件。</param>
    /// <param name="warnings">告警收集。</param>
    private static void StripColors(DesignComponent component, List<string> warnings)
    {
        foreach (var key in ColorKeys.Where(a => component.Props.ContainsKey(a)).ToList())
        {
            component.Props.Remove(key);
            warnings.Add($"组件「{component.Id}」的配色字段 {key} 已剔除：商户装修的颜色继承平台，不可自定义");
        }
    }

    /// <summary>剔除商户装修里的主题三档色并告警。</summary>
    /// <param name="theme">主题（就地清空）。</param>
    /// <param name="warnings">告警收集。</param>
    /// <remarks>
    /// 与 <see cref="StripColors"/> 同一套理由：**剔除而不是报错**。
    /// 搭建器的表单会把当前配色一起提交，直接拒绝会让店铺装修整份存不进去，
    /// 而商户真正想改的往往只是组件顺序。
    ///
    /// <para>清空成空串而不是填平台色值：配色是<b>平台级</b>配置，
    /// 抄一份进商户配置就等于将来平台改色时要同步改 N 份商户配置，
    /// 漏改一处就会有一家店颜色不对。留空让渲染端回落到平台主题才是单一来源。</para>
    /// </remarks>
    private static void StripTheme(DesignTheme theme, List<string> warnings)
    {
        foreach (var (name, value) in new[]
        {
            ("主题色", theme.Primary),
            ("TabBar 选中色", theme.TabColor),
            ("页面背景色", theme.Background),
        })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            warnings.Add($"{name} {value} 已剔除：商户装修的颜色继承平台，不可自定义");
        }

        theme.Primary = string.Empty;
        theme.TabColor = string.Empty;
        theme.Background = string.Empty;
    }

    /// <summary>收集手动指定的商品，并校验数量上限。</summary>
    /// <param name="component">组件。</param>
    /// <param name="products">商品 Id 收集。</param>
    /// <param name="errors">错误收集。</param>
    private static void CollectManualProducts(
        DesignComponent component, List<long> products, List<string> errors)
    {
        if (!component.Props.TryGetValue("manualProductIds", out var node)) return;

        if (node.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"组件「{component.Id}」的 manualProductIds 必须是数组");
            return;
        }

        var ids = node.EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out _))
            .Select(a => a.GetInt64())
            .ToList();

        // source = 1（按分类自动取）时不该传手动列表，带着也忽略，
        // 但数量超限仍然要拦——那是明显的配置错误，留着以后会变成几百个商品的巨页
        if (ids.Count > DesignComponentRegistry.MaxManualProducts)
        {
            errors.Add(
                $"组件「{component.Id}」手动指定的商品超过 " +
                $"{DesignComponentRegistry.MaxManualProducts} 个");
            return;
        }

        products.AddRange(ids);
    }
}
