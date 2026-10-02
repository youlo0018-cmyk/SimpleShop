using System.Linq.Expressions;
using Collaboration.Domain.Context;
using Collaboration.Domain.Entities;
using Collaboration.Domain.Infrastructure;
using FreeSql;

namespace Collaboration.Domain.Repository;

/// <summary>通用仓储实现（FreeSql）。查询过滤不在这里写，由 GlobalFilter 统一注册（DATA_SPEC 3.2.1）。</summary>
public abstract class CrudRepository<T> : ICrudRepository<T> where T : EntityBase, new()
{
    /// <summary>FreeSql 实例，由 Infrastructure 层注入。</summary>
    protected IFreeSql Db { get; }

    /// <summary>构造仓储。</summary>
    /// <param name="freeSql">已注册 AOP 的 FreeSql 单例。</param>
    protected CrudRepository(IFreeSql freeSql)
    {
        Db = freeSql ?? throw new ArgumentNullException(nameof(freeSql));
    }

    /// <inheritdoc />
    public async Task<T?> GetByIdAsync(long id, CancellationToken ct = default)
        => await Db.Select<T>().Where(a => a.Id == id).FirstAsync(ct);

    /// <inheritdoc />
    public async Task<long> InsertAsync(T entity, CancellationToken ct = default)
    {
        // 审计字段显式在这里填，不用 AOP 钩子。
        // 实测 FreeSql 3.5 的 Aop.CurdBefore 在本项目调用链上没有触发，
        // 依赖它会导致 Id 写成 0、CreatedAt 写成默认值 0001-01-01。
        if (entity.Id == 0) entity.Id = SnowflakeId.NewId();
        entity.CreatedAt = DateTime.UtcNow;
        ApplyCreator(entity);

        await Db.Insert(entity).ExecuteAffrowsAsync(ct);
        return entity.Id;
    }

    /// <inheritdoc />
    public async Task<int> UpdateAsync(T entity, CancellationToken ct = default)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        ApplyOperator(entity);
        return await Db.Update<T>(entity).ExecuteAffrowsAsync(ct);
    }

    /// <summary>写入创建人快照。后台实体记操作人；客户实体的用户就是 CustomerId。</summary>
    /// <param name="entity">待写入实体。</param>
    /// <remarks>创建人一旦写入便不再修改，因此只在插入时调用。</remarks>
    private static void ApplyCreator(T entity)
    {
        var ctx = TenantContextHolder.Current;

        if (entity is AdminEntityBase admin)
        {
            admin.CreatedById = ctx.UserId;
            admin.CreatedByName = ctx.UserName;
            admin.OperationId = ctx.UserId;
            admin.OperationName = ctx.UserName;
            return;
        }

        if (entity is CustomerEntityBase customer)
        {
            customer.CustomerId = ctx.UserId;
            customer.CustomerName = ctx.UserName;
        }
    }

    /// <summary>更新最后操作人快照，仅后台实体需要。</summary>
    /// <param name="entity">待更新实体。</param>
    private static void ApplyOperator(T entity)
    {
        if (entity is not AdminEntityBase admin) return;
        var ctx = TenantContextHolder.Current;
        admin.OperationId = ctx.UserId;
        admin.OperationName = ctx.UserName;
    }

    /// <inheritdoc />
    public Task<int> UpdateColumnsAsync(long id, object dto, CancellationToken ct = default)
        => Db.Update<T>().Where(a => a.Id == id).SetDto(dto).ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public Task<int> DeleteAsync(long id, CancellationToken ct = default)
        => Db.Update<T>()
            .Where(a => a.Id == id)
            .Set(a => new T { IsDeleted = true, DeletedAt = DateTime.UtcNow })
            .ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public Task<int> HardDeleteAsync(long id, CancellationToken ct = default)
        => Db.Delete<T>().Where(a => a.Id == id).ExecuteAffrowsAsync(ct);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => Db.Select<T>().Where(predicate).AnyAsync(ct);

    /// <inheritdoc />
    public Task<long> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default)
        => predicate is null
            ? Db.Select<T>().CountAsync(ct)
            : Db.Select<T>().Where(predicate).CountAsync(ct);

    /// <summary>
    /// 分页查询。
    /// </summary>
    /// <param name="page">页码，从 1 开始。注意 FreeSql 的 Page 第一参是页码不是偏移量。</param>
    /// <param name="pageSize">每页条数，1-100。</param>
    /// <param name="predicate">过滤条件，可为 null。</param>
    /// <param name="orderBy">排序表达式，可为 null（此时用调用方自行排序的查询）。</param>
    /// <param name="descending">是否倒序。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>列表与总数，total 与取数同条件。</returns>
    protected async Task<(List<T> Items, long Total)> PageQueryAsync(
        int page,
        int pageSize,
        Expression<Func<T, bool>>? predicate,
        Expression<Func<T, object>>? orderBy,
        bool descending,
        CancellationToken ct)
    {
        var select = Db.Select<T>();
        if (predicate is not null) select = select.Where(predicate);
        if (orderBy is not null) select = descending ? select.OrderByDescending(orderBy) : select.OrderBy(orderBy);

        // 追加第二排序键 Id，保证同值行的顺序稳定，避免翻页出现重复或遗漏
        select = orderBy is null ? select.OrderByDescending(a => a.Id) : select.OrderByDescending(a => a.Id);

        var total = await select.CountAsync(ct);
        var items = await select.Page(page, pageSize).ToListAsync(ct);
        return (items, total);
    }
}

