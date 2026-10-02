using System.Linq.Expressions;
using Collaboration.Domain.Entities;

namespace Collaboration.Domain.Repository;

/// <summary>通用仓储接口，所有仓储的基契约（DATA_SPEC 3.5）。软删与租户过滤由 AOP 注入，实现里不要再手写。</summary>
public interface ICrudRepository<T> where T : EntityBase, new()
{
    /// <summary>按 Id 取实体，不存在返回 null。幂等只读。</summary>
    /// <param name="id">实体 Id，雪花 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实体或 null。</returns>
    Task<T?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>插入实体，Id 与审计字段由 AOP 填充。非幂等。</summary>
    /// <param name="entity">待插入实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新实体 Id。</returns>
    Task<long> InsertAsync(T entity, CancellationToken ct = default);

    /// <summary>按字段更新实体，走 SetDto。幂等。</summary>
    /// <param name="entity">携带 Id 与待更新字段的实体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> UpdateAsync(T entity, CancellationToken ct = default);

    /// <summary>按 DTO 局部更新，只更新 DTO 中有值的字段。幂等。</summary>
    /// <param name="id">实体 Id。</param>
    /// <param name="dto">含待更新字段的对象。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> UpdateColumnsAsync(long id, object dto, CancellationToken ct = default);

    /// <summary>软删实体。幂等：重复软删第二次返回 0。</summary>
    /// <param name="id">实体 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>物理删除，仅限补偿与清理任务。幂等。</summary>
    /// <param name="id">实体 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> HardDeleteAsync(long id, CancellationToken ct = default);

    /// <summary>判断是否存在满足条件的未软删实体。幂等只读。</summary>
    /// <param name="predicate">条件表达式。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>存在返回 true。</returns>
    Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);

    /// <summary>统计满足条件的未软删实体数。幂等只读。</summary>
    /// <param name="predicate">条件表达式，可为 null 表示全部。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>条数。</returns>
    Task<long> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default);
}

