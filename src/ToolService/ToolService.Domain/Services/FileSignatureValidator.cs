namespace ToolService.Domain.Services;

/// <summary>文件魔数（magic number）校验：防伪造扩展名（DATA_SPEC 3.4 第三步）。</summary>
/// <remarks>
/// 顺序固定：扩展名白名单 → 分类大小 → 魔数。三步任何一步不过都拒绝。
/// 只校验**头部签名**，不解析完整文件结构——那属于过度校验，且对图片/文档格式支持成本高。
/// </remarks>
public static class FileSignatureValidator
{
    private static readonly Dictionary<string, byte[][]> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } },
        ["jpg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        ["jpeg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        ["gif"] = new[] { new byte[] { 0x47, 0x49, 0x46, 0x38 } },
        ["bmp"] = new[] { new byte[] { 0x42, 0x4D } },
        // webp 是 RIFF 容器，光校验 "RIFF" 不够：AVI / WAV 头部同样是 RIFF，
        // 那样 .webp 能塞进 .wav 内容骗过校验。webp 走 Matches 里的专用分支（要求 8..11 位是 "WEBP"）。
        ["webp"] = Array.Empty<byte[]>(),
        ["pdf"] = new[] { new byte[] { 0x25, 0x50, 0x44, 0x46 } },
        ["zip"] = new[] { new byte[] { 0x50, 0x4B, 0x03, 0x04 } },
        ["doc"] = new[] { new byte[] { 0xD0, 0xCF, 0x11, 0xE0 } },
        ["xls"] = new[] { new byte[] { 0xD0, 0xCF, 0x11, 0xE0 } },
        ["ppt"] = new[] { new byte[] { 0xD0, 0xCF, 0x11, 0xE0 } },
        ["mp3"] = new[] { new byte[] { 0x49, 0x44, 0x33 } },
        ["mp4"] = new[] { new byte[] { 0x66, 0x74, 0x79, 0x70 } },
        ["txt"] = Array.Empty<byte[]>()
    };

    /// <summary>
    /// 判断内容是否符合扩展名对应的魔数。
    /// </summary>
    /// <param name="extension">小写扩展名，不含点。</param>
    /// <param name="header">文件头部字节，建议取前 16 字节。</param>
    /// <returns>符合返回 true。</returns>
    /// <remarks>
    /// 扩展名不在签名表中时**拒绝**而不是放行——白名单外的格式本来就不该被允许，
    /// 到这一步说明配置漏了，放行等于没有魔数校验。
    /// txt 这类无固定签名的格式登记为空数组，表示只做扩展名与大小校验。
    /// </remarks>
    public static bool Matches(string extension, ReadOnlySpan<byte> header)
    {
        if (!Signatures.TryGetValue(extension, out var candidates)) return false;
        if (extension.Equals("webp", StringComparison.OrdinalIgnoreCase)) return IsWebp(header);
        if (candidates.Length == 0) return true;

        foreach (var signature in candidates)
        {
            if (header.Length < signature.Length) continue;
            if (header[..signature.Length].SequenceEqual(signature)) return true;
        }

        return false;
    }

    /// <summary>WebP 专用校验：RIFF????WEBP，共 12 字节。</summary>
    /// <param name="header">文件头部字节。</param>
    /// <returns>是 WebP 返回 true。</returns>
    private static bool IsWebp(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12) return false;
        return header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
            && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50;
    }

    /// <summary>
    /// 判断扩展名是否登记了魔数规则。
    /// </summary>
    /// <param name="extension">小写扩展名，不含点。</param>
    /// <returns>已登记返回 true。</returns>
    public static bool IsKnown(string extension) => Signatures.ContainsKey(extension);
}
