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

    /// <summary>
    /// 取回源时要用的 MIME 类型。
    /// </summary>
    /// <param name="ext">扩展名，不含点。</param>
    /// <returns>MIME 类型；不认识的一律回落 octet-stream。</returns>
    /// <remarks>
    /// 回源接口以前把 Content-Type 写死成 <c>application/octet-stream</c>，
    /// 于是所有图片都以「未知二进制」下发。浏览器多数时候会自己嗅探字节、
    /// 照样把图显示出来，所以**看不出问题**；但新标签页直接打开是下载而不是预览，
    /// 走严格 CSP 或中间代理的环境会直接拦掉。
    /// 扩展名在上传时已经过白名单与魔数两道校验，这里按它取类型是安全的。
    /// </remarks>
    public static string GetMimeType(string ext) => ext.ToLowerInvariant() switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "bmp" => "image/bmp",
        "webp" => "image/webp",
        "pdf" => "application/pdf",
        "txt" => "text/plain; charset=utf-8",
        "mp3" => "audio/mpeg",
        "mp4" => "video/mp4",
        "doc" => "application/msword",
        "xls" => "application/vnd.ms-excel",
        "ppt" => "application/vnd.ms-powerpoint",
        _ => "application/octet-stream"
    };
}
