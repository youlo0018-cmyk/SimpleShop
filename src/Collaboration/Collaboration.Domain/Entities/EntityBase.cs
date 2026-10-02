using FreeSql.DataAnnotations;

namespace Collaboration.Domain.Entities;

/// <summary>
/// 所有实体的根基类。
/// </summary>
/// <remarks>
/// 链路位置：所有微服务的所有实体都继承本类或其派生类。
/// 关键约束：主键由雪花 Id 生成（非自增）；时间一律存 UTC；删除默认软删。
/// 依据：DATA_SPEC.md 2.2。
/// </remarks>
public abstract class EntityBase
{
    /// <summary>
    /// 主键，雪花 Id。
    /// </summary>
    /// <remarks>
    /// 非数据库自增，由 FreeSql AOP 在 InsertBefore 注入（DATA_SPEC 3.3）。
    /// 全局 JSON 配置 WriteAsString，前端收到的是字符串，禁止 Number() 转换。
    /// </remarks>
    [Column(IsPrimary = true, IsIdentity = false, Name = "id")]
    public long Id { get; set; }

    /// <summary>
    /// 创建时间，UTC。
    /// </summary>
    /// <remarks>
    /// 写入后不再修改。展示层统一转 Asia/Shanghai（DATA_SPEC 2.8）。
    /// </remarks>
    [Column(Name = "created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 最后更新时间，UTC。
    /// </summary>
    /// <remarks>
    /// 未修改过时为 null。每次 Update 由 AOP 写入。
    /// </remarks>
    [Column(Name = "updated_at")]
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// 软删标记。
    /// </summary>
    /// <remarks>
    /// 默认 false。AOP 的软删过滤保证所有查询自动追加 IsDeleted == false。
    /// </remarks>
    [Column(Name = "is_deleted")]
    public bool IsDeleted { get; set; }

    /// <summary>
    /// 软删时间，UTC。
    /// </summary>
    /// <remarks>
    /// 未软删时为 null。写入后不再修改。
    /// </remarks>
    [Column(Name = "deleted_at")]
    public DateTime? DeletedAt { get; set; }
}

