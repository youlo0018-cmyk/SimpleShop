using System.Reflection;
using System.Text;

namespace MerchantPlatformService.Application.Features.Region;

/// <summary>
/// 内置默认地区库：<b>31 省 / 342 市 / 3056 区县</b>的完整三级数据。
/// </summary>
/// <remarks>
/// <para>数据以嵌入资源 <c>Features/Region/regions-default.json</c> 随程序发布，
/// <b>不入库</b>（DATA_SPEC 5.31「存储约定」）。平台配置里的 <c>regionsJson</c> 为空时用它回落，
/// 所以新装环境不改任何配置就能直接选省 / 市 / 区。</para>
///
/// <para><b>为什么不再硬编码在源码里</b>：这一层曾经只给了省级（34 个），
/// 理由是「市 / 区县有 3400 多条、逐年变动，硬编码会过期」。
/// 但只给省级的直接后果是<b>收货地址的三级联动根本用不了</b>：
/// 用户选完省之后没有市可选，而「运营自己去导入」既没有导入工具、也没人告诉他要导。
/// 行政区划确实会变，所以这里保持成一份**独立可整体替换的 JSON**：
/// 数据更新时只改这一个文件，不必动代码。</para>
///
/// <para>与平台自定义的关系：平台在后台「保存自定义」导入自己的数据后，
/// 读取时优先用它那一份；点「恢复默认」把 <c>regionsJson</c> 清空，就又回到这一份。</para>
/// </remarks>
public static class BuiltInRegions
{
    /// <summary>嵌入资源名。默认命名空间 + 目录 + 文件名。</summary>
    private const string ResourceName =
        "MerchantPlatformService.Application.Features.Region.regions-default.json";

    /// <summary>内置默认地区数据的 JSON 数组字符串。</summary>
    /// <remarks>
    /// 首次访问时从嵌入资源读一次并缓存：这份数据 81KB，
    /// 每次请求都反序列化 / 读流是没必要的开销，而它一旦发布就不会变。
    /// </remarks>
    public static string Json { get; } = Load();

    /// <summary>从嵌入资源读取。</summary>
    /// <returns>JSON 文本。</returns>
    /// <exception cref="InvalidOperationException">
    /// 嵌入资源缺失时抛出。这属于打包错误，必须让请求直接失败并说清原因，
    /// 而不是悄悄回落成空数据 —— 空地区库会让「选不了省市区」表现为前端 bug，排查方向完全错。
    /// </exception>
    private static string Load()
    {
        var assembly = typeof(BuiltInRegions).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);

        if (stream is null)
        {
            var available = string.Join("、", assembly.GetManifestResourceNames());
            throw new InvalidOperationException(
                $"内置地区库资源 {ResourceName} 不存在，无法回落内置默认地区。" +
                $"请检查 csproj 里的 EmbeddedResource 配置。当前可用资源：{available}");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
