using ToolService.Domain.Entities;

namespace ToolService.Domain.IRepository;

/// <summary>文件元数据仓储。</summary>
public interface IStoredFileRepository
{
    /// <summary>插入文件元数据。</summary>
    /// <param name="file">文件元数据，Id 与审计字段由 AOP 填充。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新记录 Id。</returns>
    Task<long> InsertAsync(StoredFile file, CancellationToken ct = default);

    /// <summary>按 Id 取文件元数据。</summary>
    /// <param name="id">文件 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>文件元数据或 null。</returns>
    Task<StoredFile?> GetByIdAsync(long id, CancellationToken ct = default);
}