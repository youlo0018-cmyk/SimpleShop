namespace ToolService.Application.Configuration;

/// <summary>文件存储配置（对应 AgileConfig 的 FileStorage 节）。</summary>
public sealed class FileStorageOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "FileStorage";

    /// <summary>存储后端：Local / AliyunOss / TencentCos / AzureBlob。</summary>
    public string Provider { get; set; } = "Local";

    /// <summary>本地存储根目录。</summary>
    public string LocalRoot { get; set; } = "./uploads";

    /// <summary>对外访问前缀，Local 场景由网关回源。</summary>
    public string PublicBase { get; set; } = "/gateway/files/Content";

    /// <summary>允许的扩展名，逗号分隔，不含点。</summary>
    public string AllowedExtensions { get; set; } = "png,jpg,jpeg,gif,bmp,webp,pdf,doc,xls,ppt,txt";

    /// <summary>各分类的大小上限，字节。键为 image / document / audio / video / default。</summary>
    public Dictionary<string, long> MaxSizeBytes { get; set; } = new()
    {
        ["image"] = 5L * 1024 * 1024,
        ["document"] = 20L * 1024 * 1024,
        ["audio"] = 20L * 1024 * 1024,
        ["video"] = 200L * 1024 * 1024,
        ["default"] = 10L * 1024 * 1024
    };

    /// <summary>
    /// 判断扩展名是否在白名单内。
    /// </summary>
    /// <param name="ext">小写扩展名，不含点。</param>
    /// <returns>允许返回 true。</returns>
    public bool IsExtensionAllowed(string ext)
        => AllowedExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(ext, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 取分类对应的大小限制上限。
    /// </summary>
    /// <param name="ext">扩展名。</param>
    /// <returns>字节上限；未知分类回落到 default。</returns>
    public long GetSizeLimit(string ext)
    {
        var category = Classify(ext);
        return MaxSizeBytes.TryGetValue(category, out var limit) ? limit : MaxSizeBytes["default"];
    }

    /// <summary>
    /// 归类扩展名。
    /// </summary>
    /// <param name="ext">扩展名。</param>
    /// <returns>image / document / audio / video / default。</returns>
    public static string Classify(string ext) => ext.ToLowerInvariant() switch
    {
        "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" => "image",
        "pdf" or "doc" or "xls" or "ppt" or "txt" => "document",
        "mp3" => "audio",
        "mp4" => "video",
        _ => "default"
    };
}