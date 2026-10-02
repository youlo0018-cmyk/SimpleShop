using Collaboration.Domain.Common;
using MediatR;
using Microsoft.Extensions.Options;
using ToolService.Application.Configuration;
using ToolService.Domain.Entities;
using ToolService.Domain.IRepository;
using ToolService.Domain.Services;

namespace ToolService.Application.Features.Upload;

/// <summary>统一文件上传。所有端（后台 / 小程序）都走这一个入口（DATA_SPEC 3.4）。</summary>
public record UploadFileCommand(
    string FileName,
    Stream Content,
    long Length,
    string ContentType) : IRequest<ApiResponse<UploadedFileDto>>;

/// <summary>上传结果。</summary>
public record UploadedFileDto(string FileId, string ObjectKey, string PublicUrl, long Size, string Category, string Extension);

/// <summary>上传文件处理器。</summary>
/// <remarks>
/// 三步校验顺序固定：扩展名白名单 → 分类大小 → 魔数（DATA_SPEC 3.4）。
/// 顺序不能换：先看魔数会在白名单外格式上浪费 IO，先看大小会在明显超限的大文件上白读魔数。
/// 流式处理：只读头部 16 字节做魔数校验，读完回退指针再整流写入，
/// 不把整个文件读进内存（REVIEW P1 风险「上传整文件读入内存」）。
/// </remarks>
public sealed class UploadFileHandler : IRequestHandler<UploadFileCommand, ApiResponse<UploadedFileDto>>
{
    private const int HeaderSize = 16;
    private readonly IFileStorage _storage;
    private readonly FileStorageOptions _options;
    private readonly IStoredFileRepository _files;

    /// <summary>构造处理器。</summary>
    /// <param name="storage">文件存储后端。</param>
    /// <param name="options">存储配置。</param>
    /// <param name="files">文件元数据仓储。</param>
    public UploadFileHandler(
        IFileStorage storage,
        IOptions<FileStorageOptions> options,
        IStoredFileRepository files)
    {
        _storage = storage;
        _options = options.Value;
        _files = files;
    }

    /// <summary>执行上传。</summary>
    /// <param name="request">上传命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回文件元信息；任一校验不通过返回 400。</returns>
    public async Task<ApiResponse<UploadedFileDto>> Handle(UploadFileCommand request, CancellationToken ct)
    {
        var ext = Path.GetExtension(request.FileName).TrimStart('.').ToLowerInvariant();

        // 第一步：扩展名白名单
        if (string.IsNullOrEmpty(ext) || !_options.IsExtensionAllowed(ext))
        {
            return ApiResults.Fail<UploadedFileDto>(BaseApiResponseCode.BadRequest, "不支持的文件格式");
        }

        // 第二步：分类大小上限
        var category = FileStorageOptions.Classify(ext);
        if (request.Length > _options.GetSizeLimit(ext))
        {
            return ApiResults.Fail<UploadedFileDto>(BaseApiResponseCode.BadRequest, "文件超出大小限制");
        }

        // 第三步：魔数，防伪造扩展名。
        // 注意：这里必须用 byte[] 而不是 stackalloc Span——async 方法的局部变量不能是 Span
        // （Span 指向栈，跨 await 后那块栈帧已经失效，CS4012）。堆分配的 16 字节可忽略不计。
        byte[] header = new byte[HeaderSize];
        var read = await ReadHeaderAsync(request.Content, header, ct);
        if (!FileSignatureValidator.Matches(ext, header.AsSpan(0, read)))
        {
            return ApiResults.Fail<UploadedFileDto>(BaseApiResponseCode.BadRequest, "文件内容与扩展名不匹配");
        }

        if (request.Content.CanSeek) request.Content.Seek(0, SeekOrigin.Begin);

        var objectKey = BuildObjectKey(category, ext);
        var url = await _storage.SaveAsync(objectKey, request.Content, request.ContentType, ct);

        var entity = new StoredFile
        {
            OriginalName = Path.GetFileName(request.FileName),
            ObjectKey = objectKey,
            Provider = _storage.Name,
            Category = category,
            SizeBytes = request.Length,
            Extension = ext,
            PublicUrl = url
        };

        var id = await _files.InsertAsync(entity, ct);

        var dto = new UploadedFileDto(id.ToString(), objectKey, url, entity.SizeBytes, category, ext);
        return ApiResults.Ok(dto, "上传成功");
    }

    /// <summary>读取文件头部用于魔数校验。</summary>
    /// <param name="stream">文件流。</param>
    /// <param name="buffer">目标缓冲区。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际读取的字节数。</returns>
    private static async Task<int> ReadHeaderAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (n == 0) break;
            total += n;
        }

        return total;
    }

    /// <summary>生成对象键：分类/日期/随机串.扩展名。</summary>
    /// <param name="category">分类。</param>
    /// <param name="ext">扩展名。</param>
    /// <returns>对象键。</returns>
    /// <remarks>用雪花 Id 而非时间戳做随机段，避免同毫秒并发上传互相覆盖。</remarks>
    private static string BuildObjectKey(string category, string ext)
    {
        var date = DateTime.UtcNow.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture);
        var name = Collaboration.Domain.Infrastructure.SnowflakeId.NewId().ToString();
        return $"{category}/{date}/{name}.{ext}";
    }
}
