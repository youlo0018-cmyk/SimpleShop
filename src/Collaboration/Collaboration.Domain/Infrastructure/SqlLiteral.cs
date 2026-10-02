using System.Globalization;

namespace Collaboration.Domain.Infrastructure;

/// <summary>
/// SQL 字面量安全格式化。
/// </summary>
/// <remarks>
/// 存在的理由：FreeSql 3.5.x 的 Aop.ParseExpression 只能设置字符串条件，没有参数通道，
/// 因此过滤条件必须内联为字面量。内联就意味着必须自己保证不产生注入。
/// 安全边界：只允许下面列出的值类型，且**不允许**任何来自客户端的原始字符串。
/// 允许的值来源仅三类：已验签令牌里的 long 型声明、枚举常量、服务端时钟。
/// 依据：DATA_SPEC.md 3.2（标准约束）。
/// </remarks>
internal static class SqlLiteral
{
    /// <summary>
    /// 把一个值格式化为 PostgreSQL 字面量。
    /// </summary>
    /// <param name="value">
    /// 待格式化值。允许类型：bool、int、long、short、byte、decimal、double、string、DateTime、DateTimeOffset、Guid、enum。
    /// </param>
    /// <returns>可直接嵌入 SQL 的字面量文本；类型不受支持时抛异常。</returns>
    /// <exception cref="NotSupportedException">值类型不在白名单内时抛出。</exception>
    public static string Format(object value) => value switch
    {
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        short s => s.ToString(CultureInfo.InvariantCulture),
        byte by => by.ToString(CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double db => db.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        string str => Quote(str),
        DateTime dt => Quote(dt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => Quote(dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)),
        Guid g => Quote(g.ToString()),
        Enum e => Quote(e.ToString()),
        _ => throw new NotSupportedException(
            $"SQL 字面量不支持类型 {value.GetType().FullName}；过滤条件只允许使用令牌声明、枚举常量与服务端时钟的值。")
    };

    /// <summary>
    /// 生成「列名 = 字面量」形式的条件。
    /// </summary>
    /// <param name="columnName">列名，不含引号。</param>
    /// <param name="value">比较值，见 <see cref="Format"/> 的类型白名单。</param>
    /// <returns>条件文本。</returns>
    public static string Eq(string columnName, object value) => $"{columnName} = {Format(value)}";

    private static string Quote(string raw) => $"'{raw.Replace("'", "''")}'";
}

