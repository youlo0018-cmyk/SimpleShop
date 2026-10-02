using ToolService.Application.Configuration;
using ToolService.Domain.Services;
using ToolService.Infrastructure.Storage;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>ToolService 的文件校验与本地存储测试。</summary>
/// <remarks>
/// 这些用例守的是「上传三步校验」（DATA_SPEC 3.4）与路径穿越防护。
/// 其中 webp 的 RIFF 用例是一次真实漏洞的回归测试：早期实现只比对 "RIFF" 四字节，
/// 而 WAV / AVI 同样是 RIFF 容器，导致 .webp 能塞进音频内容绕过魔数校验。
/// </remarks>
public class ToolServiceTests : IDisposable
{
    private readonly string _root;

    /// <summary>为每个用例建独立临时目录，避免用例之间互相污染。</summary>
    public ToolServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ss_tool_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>清理临时目录。</summary>
    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* 清理失败不影响断言结论 */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>把 ASCII 字符串转成字节。</summary>
    private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    /// <summary>
    /// 造一个 RIFF 容器头：前 4 字节 "RIFF"，中间 4 字节长度（任意），后接 4 字节格式标识。
    /// </summary>
    /// <param name="fourcc">RIFF 的格式标识，例如 WAVE / AVI </param>
    /// <returns>12 字节的头。</returns>
    private static byte[] Riff(string fourcc)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Ascii("RIFF"));
        bytes.AddRange(new byte[] { 0x24, 0x08, 0x00, 0x00 });
        bytes.AddRange(Ascii(fourcc));
        return bytes.ToArray();
    }

    // ---------- 魔数校验 ----------

    [Fact]
    public void Matches_PngSignature_Accepts()
    {
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];
        Assert.True(FileSignatureValidator.Matches("png", header));
    }

    [Fact]
    public void Matches_PngExtensionWithWavContent_Rejects()
    {
        Assert.False(FileSignatureValidator.Matches("png", Riff("WAVE")));
    }

    [Fact]
    public void Matches_WebpExtensionWithWavContent_Rejects()
    {
        // 回归用例：早期只看 "RIFF"，WAV 会蒙混过关。现在要求 8..11 位是 WEBP。
        Assert.False(FileSignatureValidator.Matches("webp", Riff("WAVE")));
    }

    [Fact]
    public void Matches_RealWebp_Accepts()
    {
        Assert.True(FileSignatureValidator.Matches("webp", Riff("WEBP")));
    }

    [Fact]
    public void Matches_WebpWithRiffButNotWebpMarker_Rejects()
    {
        // AVI 同为 RIFF 容器，但不是 webp
        Assert.False(FileSignatureValidator.Matches("webp", Riff("AVI ")));
    }

    [Fact]
    public void Matches_HeaderShorterThanSignature_Rejects()
    {
        // 文件本身只有 2 字节，不足以判定签名，必须拒绝而不是放行
        Assert.False(FileSignatureValidator.Matches("png", new byte[] { 0xFF, 0xD8 }));
    }

    [Fact]
    public void Matches_JpgSignature_Accepts()
    {
        Assert.True(FileSignatureValidator.Matches("jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
    }

    [Fact]
    public void Matches_PdfSignature_Accepts()
    {
        Assert.True(FileSignatureValidator.Matches("pdf", Ascii("%PDF-1.7")));
    }

    [Fact]
    public void Matches_TxtHasNoSignature_AcceptsAnything()
    {
        // txt 登记为空签名数组，表示只做扩展名 + 大小校验
        Assert.True(FileSignatureValidator.Matches("txt", new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void Matches_ExtensionNotInTable_Rejects()
    {
        // 表里没有的格式一律拒绝：到这一步说明白名单漏配了，放行等于没有校验
        Assert.False(FileSignatureValidator.Matches("exe", Ascii("MZ90")));
    }

    [Fact]
    public void Matches_IsCaseInsensitiveOnExtension()
    {
        Assert.True(FileSignatureValidator.Matches("PNG", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
    }

    [Fact]
    public void IsKnown_ReflectsSignatureTable()
    {
        Assert.True(FileSignatureValidator.IsKnown("png"));
        Assert.False(FileSignatureValidator.IsKnown("exe"));
    }

    // ---------- 扩展名白名单与分类 ----------

    [Fact]
    public void IsExtensionAllowed_AcceptsWhitelisted()
    {
        var o = new FileStorageOptions();
        Assert.True(o.IsExtensionAllowed("png"));
        Assert.True(o.IsExtensionAllowed("PDF"));
    }

    [Fact]
    public void IsExtensionAllowed_RejectsNonWhitelisted()
    {
        var o = new FileStorageOptions();
        Assert.False(o.IsExtensionAllowed("exe"));
        Assert.False(o.IsExtensionAllowed(""));
    }

    [Theory]
    [InlineData("png", "image")]
    [InlineData("webp", "image")]
    [InlineData("pdf", "document")]
    [InlineData("mp3", "audio")]
    [InlineData("mp4", "video")]
    [InlineData("exe", "default")]
    public void Classify_MapsToExpectedCategory(string ext, string expected)
    {
        Assert.Equal(expected, FileStorageOptions.Classify(ext));
    }

    [Fact]
    public void GetSizeLimit_UsesCategoryLimit()
    {
        var o = new FileStorageOptions();
        Assert.Equal(5L * 1024 * 1024, o.GetSizeLimit("png"));
        Assert.Equal(200L * 1024 * 1024, o.GetSizeLimit("mp4"));
    }

    [Fact]
    public void GetSizeLimit_UnknownCategoryFallsBackToDefault()
    {
        var o = new FileStorageOptions();
        o.MaxSizeBytes.Remove("image");
        Assert.Equal(o.MaxSizeBytes["default"], o.GetSizeLimit("png"));
    }

    // ---------- 本地存储 ----------

    [Fact]
    public async Task LocalFileStorage_SaveThenRead_RoundTripsContent()
    {
        var storage = new LocalFileStorage(_root, "/gateway/files/Content");
        byte[] payload = Ascii("hello simpleshop");

        string url = await storage.SaveAsync("image/2026/01/01/x.txt", new MemoryStream(payload), "text/plain");

        Assert.Equal("/gateway/files/Content/image/2026/01/01/x.txt", url);
        var read = await storage.OpenReadAsync("image/2026/01/01/x.txt");
        Assert.NotNull(read);
        using var ms = new MemoryStream();
        await read!.CopyToAsync(ms);
        Assert.Equal(payload, ms.ToArray());
    }

    [Fact]
    public async Task LocalFileStorage_Delete_RemovesFile()
    {
        var storage = new LocalFileStorage(_root, "/p");
        await storage.SaveAsync("a/b.txt", new MemoryStream(Ascii("x")), "text/plain");

        Assert.True(await storage.DeleteAsync("a/b.txt"));
        Assert.False(await storage.DeleteAsync("a/b.txt"));
        Assert.Null(await storage.OpenReadAsync("a/b.txt"));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("image/../../escape.txt")]
    public async Task LocalFileStorage_PathTraversal_Throws(string key)
    {
        var storage = new LocalFileStorage(_root, "/p");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.SaveAsync(key, new MemoryStream(Ascii("x")), "text/plain"));
    }

    [Fact]
    public async Task LocalFileStorage_SiblingDirectoryWithSamePrefix_Throws()
    {
        // 回归用例：早期只做 full.StartsWith(root)，兄弟目录 uploads-evil 能通过 uploads 的前缀检查。
        var storage = new LocalFileStorage(Path.Combine(_root, "uploads"), "/p");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.SaveAsync("../uploads-evil/x.txt", new MemoryStream(Ascii("x")), "text/plain"));
    }

    [Fact]
    public async Task LocalFileStorage_OpenRead_MissingObject_ReturnsNull()
    {
        var storage = new LocalFileStorage(_root, "/p");
        Assert.Null(await storage.OpenReadAsync("image/nope.png"));
    }
}
