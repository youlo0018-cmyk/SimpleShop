using Collaboration.Domain.Infrastructure;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>SQL 字面量格式化的单元测试（依据 DATA_SPEC 3.2）。</summary>
public class SqlLiteralTests
{
    [Fact]
    public void Format_布尔值_输出小写字面量()
    {
        Assert.Equal("true", SqlLiteral.Format(true));
        Assert.Equal("false", SqlLiteral.Format(false));
    }

    [Fact]
    public void Format_整数_不受区域设置影响()
    {
        Assert.Equal("1234567890123", SqlLiteral.Format(1234567890123L));
        Assert.Equal("-1", SqlLiteral.Format(-1));
    }

    [Fact]
    public void Format_字符串_单引号被转义()
    {
        Assert.Equal("'O''Brien'", SqlLiteral.Format("O'Brien"));
    }

    [Fact]
    public void Format_字符串含注入片段_被转义为无害字面量()
    {
        var result = SqlLiteral.Format("'; DROP TABLE product; --");
        Assert.Equal("'''; DROP TABLE product; --'", result);
    }

    [Fact]
    public void Format_枚举_输出名称字符串()
    {
        Assert.Equal("'Enabled'", SqlLiteral.Format(DemoStatus.Enabled));
    }

    [Fact]
    public void Format_不受支持类型_抛异常()
    {
        Assert.Throws<NotSupportedException>(() => SqlLiteral.Format(new object()));
    }

    [Fact]
    public void Eq_生成列等于值()
    {
        Assert.Equal("platform_id = 42", SqlLiteral.Eq("platform_id", 42L));
    }

    private enum DemoStatus
    {
        Disabled = 1,
        Enabled = 2
    }
}

