namespace ToolService.Infrastructure.Storage;

/// <summary>本地磁盘存储。开发环境默认后端。</summary>
/// <remarks>
/// 放在 Infrastructure 而非 Domain：Domain 层不应出现文件系统这类 IO 实现（CODE_STANDARD 分层规则）。
/// 路径穿越防护在 ResolvePath：objectKey 来自客户端，必须确保拼不出根目录之外的路径。
/// </remarks>
public sealed class LocalFileStorage : ToolService.Domain.Services.IFileStorage
{
    private readonly string _root;
    private readonly string _publicBase;

    /// <summary>构造本地存储。</summary>
    /// <param name="root">磁盘根目录。</param>
    /// <param name="publicBase">对外访问前缀，如 /gateway/files/Content。</param>
    public LocalFileStorage(string root, string publicBase)
    {
        _root = root;
        _publicBase = publicBase.TrimEnd('/');
    }

    /// <inheritdoc />
    public string Name => "Local";

    /// <inheritdoc />
    public async Task<string> SaveAsync(string objectKey, Stream content, string contentType, CancellationToken ct = default)
    {
        var full = ResolvePath(objectKey);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var fs = File.Create(full);
        await content.CopyToAsync(fs, ct);

        return _publicBase + "/" + objectKey;
    }

    /// <inheritdoc />
    public Task<Stream?> OpenReadAsync(string objectKey, CancellationToken ct = default)
    {
        var full = ResolvePath(objectKey);
        if (!File.Exists(full)) return Task.FromResult<Stream?>(null);
        return Task.FromResult<Stream?>(File.OpenRead(full));
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        var full = ResolvePath(objectKey);
        if (!File.Exists(full)) return Task.FromResult(false);
        File.Delete(full);
        return Task.FromResult(true);
    }

    /// <summary>把对象键解析成磁盘绝对路径，并阻断路径穿越。</summary>
    /// <param name="objectKey">对象键。</param>
    /// <returns>绝对路径。</returns>
    /// <exception cref="InvalidOperationException">对象键试图跳出根目录时抛出。</exception>
    private string ResolvePath(string objectKey)
    {
        var root = Path.GetFullPath(_root);
        var full = Path.GetFullPath(Path.Combine(root, objectKey));

        // 路径穿越防护：..\ 或绝对路径都不能跳出根目录。
        // 注意要带分隔符比较，否则 C:\uploads-evil 会通过 C:\uploads 的前缀检查。
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"非法的对象键：{objectKey}");
        }

        return full;
    }
}