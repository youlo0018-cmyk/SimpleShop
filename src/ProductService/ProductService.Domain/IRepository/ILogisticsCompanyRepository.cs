using ProductService.Domain.Entities;

namespace ProductService.Domain.IRepository;

/// <summary>物流公司字典仓储（DATA_SPEC 5.23）。发货表单下拉的数据源。</summary>
public interface ILogisticsCompanyRepository
{
    /// <summary>分页查询物流公司。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数，1-100。</param>
    /// <param name="keyword">按公司名模糊搜索。</param>
    /// <param name="status">状态过滤，0 表示不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>列表与总数。幂等只读。</returns>
    Task<(List<LogisticsCompany> Items, long Total)> QueryPagedAsync(
        int page, int pageSize, string keyword, int status, CancellationToken ct = default);

    /// <summary>取启用中的物流公司，供发货表单下拉使用。</summary>
    /// <param name="keyword">按公司名模糊搜索，空表示不过滤。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>按排序值升序的公司列表。幂等只读。</returns>
    /// <remarks>
    /// **只返回启用的**：停用的公司不该再出现在发货表单里，但已发货的单
    /// 存的是名称快照，不受影响，所以这里过滤掉不会丢历史信息。
    /// </remarks>
    Task<List<LogisticsCompany>> ListEnabledAsync(string keyword, CancellationToken ct = default);

    /// <summary>按 Id 取物流公司。</summary>
    /// <param name="id">物流公司 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>公司或 null。幂等只读。</returns>
    Task<LogisticsCompany?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>判断公司名是否已被占用。</summary>
    /// <param name="companyName">公司名。</param>
    /// <param name="excludeId">排除的 Id（编辑时传自身），可为 0。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>存在返回 true。幂等只读。</returns>
    Task<bool> ExistsByNameAsync(string companyName, long excludeId = 0, CancellationToken ct = default);

    /// <summary>插入物流公司。</summary>
    /// <param name="company">实体，Id 与审计字段由仓储基类填充。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新公司 Id。非幂等。</returns>
    Task<long> InsertAsync(LogisticsCompany company, CancellationToken ct = default);

    /// <summary>按字段更新物流公司。</summary>
    /// <param name="company">携带 Id 与待更新字段的实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    Task<int> UpdateAsync(LogisticsCompany company, CancellationToken ct = default);

    /// <summary>软删物流公司。</summary>
    /// <param name="id">公司 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。幂等。</returns>
    /// <remarks>软删而不是物理删：名称唯一索引是 partial 的，软删后同名可重新添加。</remarks>
    Task<int> DeleteAsync(long id, CancellationToken ct = default);
}
