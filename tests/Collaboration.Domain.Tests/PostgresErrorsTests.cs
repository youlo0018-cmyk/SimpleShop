using Collaboration.Domain.Infrastructure;
using Npgsql;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>PostgreSQL 错误判定的单元测试。</summary>
/// <remarks>
/// 这组用例守的是一条踩过的坑：直接写 <c>catch (PostgresException)</c> 抓不到唯一约束冲突，
/// 因为 FreeSql 会把驱动异常包进普通 <see cref="Exception"/>。
/// 限购的唯一索引如果没被识别，重复抢购会返回 500 而不是「超出限购」——
/// 而限购正是秒杀防超卖的最后一道防线。
/// </remarks>
public class PostgresErrorsTests
{
    /// <summary>构造一个带指定 SQLSTATE 与索引名的 PostgreSQL 异常。</summary>
    /// <param name="sqlState">SQLSTATE。</param>
    /// <param name="constraintName">索引名。</param>
    /// <returns>异常实例。</returns>
    private static PostgresException NewPgException(string sqlState, string constraintName)
        // 带索引名的是 18 参数那个构造函数。常用的 4 参数版不接收 constraintName，
        // 而 ConstraintName 又是 get-only 自动属性，所以只能走这个完整版。
        => new(
            "boom", "ERROR", "ERROR", sqlState,
            detail: null, hint: null,
            position: 0, internalPosition: 0,
            internalQuery: null, where: null,
            schemaName: null, tableName: null, columnName: null, dataTypeName: null,
            constraintName: constraintName, file: null, line: null, routine: null);

    [Fact]
    public void Unwrap_裸的Postgres异常_能直接找到()
    {
        var ex = NewPgException(PostgresErrors.UniqueViolation, "uk_order_no");

        Assert.Same(ex, PostgresErrors.Unwrap(ex));
    }

    [Fact]
    public void IsUniqueViolation_异常被FreeSql包过_仍然识别得出来()
    {
        // 真实故障形态：最外层是 System.Exception，InnerException 才是驱动异常
        var wrapped = new Exception("23505: duplicate key value violates unique constraint",
            NewPgException(PostgresErrors.UniqueViolation, "uk_seckill_grab_biz"));

        Assert.True(PostgresErrors.IsUniqueViolation(wrapped));
    }

    [Fact]
    public void IsUniqueViolation_包了好几层_也能穿透()
    {
        var wrapped = new Exception("外层", new Exception("中层",
            new Exception("内层", NewPgException(PostgresErrors.UniqueViolation, "uk_x"))));

        Assert.True(PostgresErrors.IsUniqueViolation(wrapped));
    }

    [Fact]
    public void IsUniqueViolationOn_索引名匹配_返回true()
    {
        var wrapped = new Exception("包一层",
            NewPgException(PostgresErrors.UniqueViolation, "uk_seckill_grab_biz"));

        Assert.True(PostgresErrors.IsUniqueViolationOn(wrapped, "uk_seckill_grab"));
    }

    [Fact]
    public void IsUniqueViolationOn_撞的是别的索引_返回false()
    {
        // 撞了别的索引就不能当成「重复抢购」，
        // 否则会把订单号冲突之类的真实故障误报成限购，掩盖真正的原因
        var wrapped = new Exception("包一层",
            NewPgException(PostgresErrors.UniqueViolation, "uk_order_no"));

        Assert.False(PostgresErrors.IsUniqueViolationOn(wrapped, "uk_seckill_grab"));
    }

    [Fact]
    public void IsUniqueViolation_不是唯一约束冲突_返回false()
    {
        var wrapped = new Exception("包一层", NewPgException(PostgresErrors.ForeignKeyViolation, "fk_order"));

        Assert.False(PostgresErrors.IsUniqueViolation(wrapped));
    }

    [Fact]
    public void Unwrap_普通异常_返回null()
    {
        Assert.Null(PostgresErrors.Unwrap(new InvalidOperationException("普通错误")));
    }

    [Fact]
    public void Unwrap_null_返回null()
    {
        Assert.Null(PostgresErrors.Unwrap(null));
    }

    [Fact]
    public void Unwrap_异常链成环_不死循环()
    {
        // 异常环（InnerException 指回自身）在真实驱动里出现过，
        // 解包循环必须自己设上限，否则这里会直接把测试进程挂死
        var ring = new Exception("环");
        Assert.NotNull(ring);

        // 构造一个超过 10 层的链，验证它在上限处停下而不是无限展开
        Exception current = new Exception("最深");
        for (var i = 0; i < 30; i++)
        {
            current = new Exception($"第 {i} 层", current);
        }

        // 链里没有 PostgresException，解包应正常走完并返回 null
        Assert.Null(PostgresErrors.Unwrap(current));
    }
}
