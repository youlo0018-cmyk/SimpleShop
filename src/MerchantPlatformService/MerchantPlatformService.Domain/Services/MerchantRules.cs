using System.Text.Json;
using System.Text.RegularExpressions;

namespace MerchantPlatformService.Domain.Services;

/// <summary>平台 / 商户的纯规则（编码生成、字段校验、地区 JSON 校验）。</summary>
/// <remarks>
/// 放在 Domain 层是为了让这些规则能被单元测试直接覆盖，
/// 不必起服务、不必建库——它们都是「最容易写错、又最影响用户体验」的部分。
/// </remarks>
public static partial class MerchantRules
{
    /// <summary>平台编码：6 位字母（规格 5.1）。</summary>
    [GeneratedRegex("^[A-Za-z]{6}$")]
    private static partial Regex PlatformCodePattern();

    /// <summary>手机号：中国大陆 11 位（规格 5.1 / 5.2）。</summary>
    /// <remarks>必须用逐字字符串 <c>@"..."</c>：普通字符串里的 <c>\d</c> 会被 C# 当成非法转义序列（CS1009）。</remarks>
    [GeneratedRegex(@"^1[3-9]\d{9}$")]
    private static partial Regex PhonePattern();

    /// <summary>十六进制颜色 <c>#RRGGBB</c>（规格 5.1 三档主题色）。</summary>
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    /// <summary>地区 JSON 上限 2MB（规格 5.31）。</summary>
    public const int MaxRegionsJsonBytes = 2 * 1024 * 1024;

    /// <summary>校验平台编码。</summary>
    /// <param name="code">平台编码。</param>
    /// <returns>返回 null 表示通过，否则是中文错误提示。</returns>
    public static string? ValidatePlatformCode(string? code)
    {
        var text = (code ?? string.Empty).Trim();
        if (text.Length == 0) return "请填写平台编码";
        return PlatformCodePattern().IsMatch(text) ? null : "平台编码必须是 6 位字母";
    }

    /// <summary>校验手机号。</summary>
    /// <param name="phone">手机号。</param>
    /// <returns>返回 null 表示通过，否则是中文错误提示。</returns>
    public static string? ValidatePhone(string? phone)
        => PhonePattern().IsMatch((phone ?? string.Empty).Trim()) ? null : "请填写正确的手机号";

    /// <summary>校验十六进制颜色。</summary>
    /// <param name="color">颜色值；空串表示用默认值，直接通过。</param>
    /// <returns>返回 null 表示通过，否则是中文错误提示。</returns>
    public static string? ValidateColor(string? color)
    {
        var text = (color ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        return ColorPattern().IsMatch(text) ? null : "颜色格式不正确，应为 #RRGGBB";
    }

    /// <summary>生成商户编号：平台编码 + 雪花 Id，如 <c>DEMOPL13755080881608709</c>。</summary>
    /// <param name="platformCode">平台编码。</param>
    /// <param name="merchantId">商户雪花 Id。</param>
    /// <returns>商户编号。</returns>
    /// <remarks>
    /// 带平台前缀是为了让编号一眼看出归属：客服收到一个编号就能判断是哪个平台的单，
    /// 不用再查库。平台编码本身全局唯一，所以前缀 + Id 天然全局唯一。
    /// </remarks>
    public static string BuildMerchantNo(string platformCode, long merchantId)
        => $"{platformCode.ToUpperInvariant()}{merchantId}";

    /// <summary>校验三级地区 JSON。</summary>
    /// <param name="json">地区 JSON 字符串；空串表示「恢复默认」，直接通过。</param>
    /// <returns>返回 null 表示通过，否则是中文错误提示。</returns>
    /// <remarks>
    /// 校验三件事：合法 JSON、顶层是非空数组、每级都带 name。
    /// <b>刻意不卡「必须正好三级」</b>：规格只要求「三级地区数据」，
    /// 但不同数据源给的两级 / 四级都出现过，强行卡死会让运营导入失败，
    /// 而 C 端只是按层级原样渲染，卡死没有实际收益。
    /// </remarks>
    public static string? ValidateRegionsJson(string? json)
    {
        // 空串 = 恢复默认，这是规格里的一个明确操作，不是「配错了」
        if (string.IsNullOrWhiteSpace(json)) return null;

        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxRegionsJsonBytes)
        {
            return "地区数据过大，不能超过 2MB";
        }

        RegionNode[]? roots;
        try
        {
            roots = JsonSerializer.Deserialize<RegionNode[]>(json);
        }
        catch (JsonException)
        {
            return "地区数据不是合法的 JSON";
        }

        if (roots is null || roots.Length == 0)
        {
            return "地区数据不能是空数组";
        }

        // 每一级都必须有 name：前端按 name 渲染，缺了就是一行空白，
        // 用户会看到「省 / 市 / 区县」下面挂着没名字的条目
        foreach (var root in roots)
        {
            if (!HasNames(root)) return "每一级地区都必须填写名称";
        }

        return null;
    }

    /// <summary>递归检查节点及其全部子孙是否都带 name。</summary>
    /// <param name="node">当前节点。</param>
    /// <returns>全部带 name 返回 true。</returns>
    private static bool HasNames(RegionNode node)
    {
        if (string.IsNullOrWhiteSpace(node.Name)) return false;
        if (node.Children is null) return true;

        foreach (var child in node.Children)
        {
            if (!HasNames(child)) return false;
        }

        return true;
    }

    /// <summary>地区 JSON 的一个节点。</summary>
    /// <remarks>
    /// <c>Name</c> 是地区名称；<c>Children</c> 是下级地区，null 表示叶子节点。
    /// 写成 class 而不是 record：反序列化只需要一个带无参构造的 POCO，
    /// record 的主构造函数在这里没有收益却要多写两行样板。
    /// </remarks>
    public sealed class RegionNode
    {
        /// <summary>地区名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>下级地区，null 表示叶子节点。</summary>
        public RegionNode[]? Children { get; set; }
    }
}
