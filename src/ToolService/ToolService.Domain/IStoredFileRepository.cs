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

    /// <summary>后台分页查文件（文件管理页）。</summary>
    /// <param name="page">页码，从 1 起。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="keyword">按原始文件名模糊搜索。</param>
    /// <param name="category">分类过滤，空表示不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>文件元数据列表与总数。幂等只读。</returns>
    Task<(List<StoredFile> Items, long Total)> PageAsync(
        int page, int pageSize, string keyword, string category, CancellationToken ct = default);

    /// <summary>软删文件元数据。</summary>
    /// <param name="id">文件 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    /// <remarks>
    /// 软删而不是物理删：<b>对象存储里的文件内容一律不删</b>。
    /// 元数据被别的业务引用着（商品主图、评价图），删了内容会让那些页面集体裂图，
    /// 而「这个文件我不用了」和「这个文件可以物理销毁」是两件事，后者需要引用计数。
    /// 引用计数属于下一阶段的存储治理，不塞进后台管理页里。
    /// </remarks>
    Task<int> DeleteAsync(long id, CancellationToken ct = default);
}
