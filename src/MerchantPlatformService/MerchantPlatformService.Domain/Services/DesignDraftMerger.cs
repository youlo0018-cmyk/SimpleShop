using System.Text.Json;
using System.Text.Json.Nodes;

namespace MerchantPlatformService.Domain.Services;

/// <summary>把「按页增量提交」的装修配置合并到已存草稿上。</summary>
/// <remarks>
/// 后台搭建器一次只提交当前编辑的那一页（<c>{ pages: { index: {...} } }</c>），
/// 因为整份提交会让「切到我的页存草稿」把首页的排版一起覆盖掉。
/// 但后端校验器是按<b>完整配置</b>校验的（平台装修必须同时有 index / profile 两页），
/// 于是「只发一页」在服务端就成了「缺少页面画布」，存草稿 100% 失败 ——
/// 而 API 层的 e2e 一直是拿完整配置测的，所以这条从来没被红过。
///
/// <para>合并规则（按属性递归一层）：</para>
/// <list type="bullet">
/// <item><c>pages</c> <b>逐页覆盖</b>：请求里出现的页整页替换，没出现的页保留原值；</item>
/// <item>其余顶层属性（<c>theme</c> / <c>tabBar</c> / <c>regions</c> / <c>platformCode</c>）
/// 请求里<b>显式带了就覆盖</b>，没带就保留原值。搭建器不编辑主题与 TabBar，
/// 不带就保留是唯一正确的选择：不然每存一次草稿都会把它们清空。</item>
/// </list>
/// </remarks>
public static class DesignDraftMerger
{
    /// <summary>合并已存配置与本次提交的配置。</summary>
    /// <param name="storedJson">已存草稿（或已发布版本）JSON；空表示还没有任何配置。</param>
    /// <param name="incomingJson">本次提交的 JSON。</param>
    /// <returns>合并后的 JSON。任一侧不是合法 JSON 时抛 <see cref="JsonException"/>，由调用方转成业务错误。</returns>
    public static string Merge(string? storedJson, string? incomingJson)
    {
        var stored = ParseObject(storedJson);
        var incoming = ParseObject(incomingJson);

        if (incoming is null) return stored?.ToJsonString() ?? "{}";
        if (stored is null) return incoming.ToJsonString();

        foreach (var (key, value) in incoming.ToList())
        {
            if (key == "pages" && value is JsonObject incomingPages)
            {
                var mergedPages = stored["pages"] as JsonObject ?? [];
                foreach (var (pageKey, pageValue) in incomingPages.ToList())
                {
                    mergedPages[pageKey] = pageValue?.DeepClone();
                }
                stored["pages"] = mergedPages;
                continue;
            }

            stored[key] = value?.DeepClone();
        }

        return stored.ToJsonString();
    }

    /// <summary>解析成 JSON 对象；空串返回 null，非对象抛 <see cref="JsonException"/>。</summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>对象节点或 null。</returns>
    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var node = JsonNode.Parse(json);
        return node as JsonObject
            ?? throw new JsonException("装修配置必须是一个 JSON 对象");
    }
}
