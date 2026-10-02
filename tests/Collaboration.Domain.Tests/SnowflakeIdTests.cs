using Collaboration.Domain.Infrastructure;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>雪花 Id 的单元测试（依据 DATA_SPEC 3.3、3.4）。</summary>
public class SnowflakeIdTests
{
    [Fact]
    public void 完整生命周期_未配置先失败_配置后递增_重复配置被拒()
    {
        var before = Assert.Throws<InvalidOperationException>(() => SnowflakeId.NewId());
        Assert.Contains("Configure", before.Message);

        SnowflakeId.Configure(9);
        Assert.Equal(9, SnowflakeId.WorkerId);

        var first = SnowflakeId.NewId();
        var second = SnowflakeId.NewId();
        Assert.True(second > first, "连续两次生成的 Id 应严格递增");

        var again = Assert.Throws<InvalidOperationException>(() => SnowflakeId.Configure(8));
        Assert.Contains("不允许重复配置", again.Message);
    }
}

