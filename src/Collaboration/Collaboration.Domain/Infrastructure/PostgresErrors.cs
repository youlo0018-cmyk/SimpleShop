using Npgsql;

namespace Collaboration.Domain.Infrastructure;

/// <summary>PostgreSQL 错误码判定。</summary>
/// <remarks>
/// <b>为什么需要「解包」而不是直接 <c>catch (PostgresException)</c>：</b>
/// FreeSql 的 <c>AdoProvider</c> 在部分路径上会把驱动异常包进一个普通
/// <see cref="Exception"/> 再抛出，此时最外层类型是 <c>System.Exception</c>，
/// <c>InnerException</c> 才是 <c>Npgsql.PostgresException</c>。
/// 直接按类型 catch 会**静默漏掉**唯一索引冲突——本项目真实踩过：
/// 秒杀抢购的限购唯一索引冲突没被识别，重复抢购返回 HTTP 500 而不是「超出限购」。
/// </remarks>
public static class PostgresErrors
{
    /// <summary>唯一约束冲突。</summary>
    public const string UniqueViolation = "23505";

    /// <summary>外键约束冲突。</summary>
    public const string ForeignKeyViolation = "23503";

    /// <summary>在异常链（含自身）里找 PostgreSQL 异常。</summary>
    /// <param name="ex">任意异常。</param>
    /// <returns>找到返回该异常，没找到返回 null。</returns>
    public static PostgresException? Unwrap(Exception? ex)
    {
        // 防御性上限：异常环（InnerException 指回自身）会让 while 死循环
        for (var i = 0; ex is not null && i < 10; i++)
        {
            if (ex is PostgresException pg) return pg;
            ex = ex.InnerException;
        }

        return null;
    }

    /// <summary>是否撞了唯一索引。</summary>
    /// <param name="ex">任意异常，可能被 FreeSql 包过。</param>
    /// <returns>是唯一约束冲突返回 true。</returns>
    public static bool IsUniqueViolation(Exception? ex)
        => Unwrap(ex)?.SqlState == UniqueViolation;

    /// <summary>是否撞了某个指定名字的唯一索引。</summary>
    /// <param name="ex">任意异常，可能被 FreeSql 包过。</param>
    /// <param name="constraintName">索引名，按包含匹配。</param>
    /// <returns>撞的是该索引返回 true。</returns>
    public static bool IsUniqueViolationOn(Exception? ex, string constraintName)
    {
        var pg = Unwrap(ex);
        return pg is not null
            && pg.SqlState == UniqueViolation
            && (pg.ConstraintName ?? string.Empty).Contains(constraintName, StringComparison.Ordinal);
    }
}
