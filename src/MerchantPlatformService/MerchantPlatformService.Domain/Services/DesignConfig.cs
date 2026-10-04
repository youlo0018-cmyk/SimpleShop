using System.Text.Json;
using System.Text.Json.Serialization;

namespace MerchantPlatformService.Domain.Services;

/// <summary>装修配置（BUSINESS.md 16.5 的 JSON 结构）。</summary>
/// <remarks>
/// <b>所有键名都显式标注 <see cref="JsonPropertyNameAttribute"/></b>——见 CODING_STANDARD §6 第 39 条。
/// 这份 JSON 一头连后台搭建器、一头连 C 端渲染器，是<b>跨两端契约</b>；
/// 而 <c>JsonSerializer</c> 默认区分大小写，漏标一个键就是「配置读出来全是默认值」。
/// </remarks>
public sealed class DesignConfig
{
    /// <summary>平台编码（商户装修为空）。</summary>
    [JsonPropertyName("platformCode")]
    public string PlatformCode { get; set; } = string.Empty;

    /// <summary>已发布版本号，发布时递增。</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>主题色。</summary>
    [JsonPropertyName("theme")]
    public DesignTheme Theme { get; set; } = new();

    /// <summary>底部导航。</summary>
    [JsonPropertyName("tabBar")]
    public List<DesignTabItem> TabBar { get; set; } = [];

    /// <summary>各页画布。</summary>
    [JsonPropertyName("pages")]
    public Dictionary<string, DesignPage> Pages { get; set; } = [];

    /// <summary>地区地址。</summary>
    [JsonPropertyName("regions")]
    public DesignRegions Regions { get; set; } = new();

    /// <summary>序列化成 JSON 字符串。</summary>
    /// <returns>配置 JSON。</returns>
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>从 JSON 字符串反序列化。</summary>
    /// <param name="json">配置 JSON；空串返回空配置。</param>
    /// <returns>配置对象。</returns>
    public static DesignConfig FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new DesignConfig();
        return JsonSerializer.Deserialize<DesignConfig>(json) ?? new DesignConfig();
    }
}

/// <summary>主题色三档（写入 CSS 变量覆盖默认 token）。</summary>
public sealed class DesignTheme
{
    /// <summary>主色。</summary>
    [JsonPropertyName("primary")]
    public string Primary { get; set; } = string.Empty;

    /// <summary>TabBar 选中色。</summary>
    [JsonPropertyName("tabColor")]
    public string TabColor { get; set; } = string.Empty;

    /// <summary>页面背景色。</summary>
    [JsonPropertyName("background")]
    public string Background { get; set; } = string.Empty;
}

/// <summary>TabBar 一项。</summary>
public sealed class DesignTabItem
{
    /// <summary>页面路径。</summary>
    [JsonPropertyName("pagePath")]
    public string PagePath { get; set; } = string.Empty;

    /// <summary>文案。</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>图标路径（微信小程序要求 PNG）。</summary>
    [JsonPropertyName("iconPath")]
    public string IconPath { get; set; } = string.Empty;
}

/// <summary>一个可搭建页面的画布。</summary>
public sealed class DesignPage
{
    /// <summary>组件列表，画布是**单层扁平**结构，不做嵌套容器。</summary>
    [JsonPropertyName("components")]
    public List<DesignComponent> Components { get; set; } = [];
}

/// <summary>画布上的一个组件实例。</summary>
public sealed class DesignComponent
{
    /// <summary>实例标识，画布内唯一，≤ 32 位。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>组件类型，必须是注册表里已注册的。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>占用栅格列数，只能取 1/2/3/4/6/12。</summary>
    [JsonPropertyName("span")]
    public int Span { get; set; } = 12;

    /// <summary>固定高度（px），留空表示按组件默认。</summary>
    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>组件私有配置，由各组件的 Validator 校验。</summary>
    [JsonPropertyName("props")]
    public Dictionary<string, JsonElement> Props { get; set; } = [];
}

/// <summary>装修配置里的地区地址引用。</summary>
public sealed class DesignRegions
{
    /// <summary>地区 JSON；空字符串表示使用内置默认。</summary>
    [JsonPropertyName("customJson")]
    public string CustomJson { get; set; } = string.Empty;
}
