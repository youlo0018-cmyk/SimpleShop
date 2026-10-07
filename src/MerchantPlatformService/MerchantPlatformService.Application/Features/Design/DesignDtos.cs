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
/// <summary>组件库条目。</summary>
/// <param name="Type">组件类型。</param>
/// <param name="Name">中文名。</param>
/// <param name="Category">所属分类。</param>
/// <param name="Props">可编辑属性 schema，驱动后台右侧属性面板。</param>
/// <remarks>
/// schema 由后端下发而不是前端按 type 写死分支：组件注册表加了新组件却忘了在
/// 属性面板补一段，运营就只能拖出一个改不了内容的组件，而界面上看不出哪里不对。
/// </remarks>
public sealed record ComponentDefDto(
    string Type, string Name, string Category,
    IReadOnlyList<MerchantPlatformService.Domain.Services.DesignPropDef> Props);
