namespace MerchantPlatformService.Application.Features.Design;

/// <summary>装修配置读取结果。</summary>
/// <param name="ConfigJson">当前生效的配置 JSON（草稿优先）。</param>
/// <param name="Version">已发布版本号，0 表示从未发布。</param>
/// <param name="HasDraft">是否存在未发布的草稿（后台据此提示「有改动未发布」）。</param>
/// <param name="Warnings">最近一次保存的告警（如商户配色被剔除）。</param>
public sealed record DesignResult(
    string ConfigJson, int Version, bool HasDraft, IReadOnlyList<string> Warnings);

/// <summary>组件定义（后台组件库展示）。</summary>
/// <param name="Type">组件类型。</param>
/// <param name="Name">中文名。</param>
/// <param name="Category">分类。</param>
public sealed record ComponentDefDto(string Type, string Name, string Category);
