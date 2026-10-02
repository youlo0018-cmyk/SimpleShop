using Collaboration.Domain.Configuration;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>配置完整性校验的单元测试（依据 DATA_SPEC 1.2 的 S3）。</summary>
public class ConfigurationValidatorTests
{
    private static Dictionary<string, string?> Complete() => new()
    {
        ["ConnectionStrings:Default"] = "Host=127.0.0.1;Database=simpleshopcustomer",
        ["Redis:ConnectionString"] = "127.0.0.1:6379",
        ["Consul:Address"] = "http://127.0.0.1:8500",
        ["RabbitMq:Host"] = "localhost"
    };

    [Fact]
    public void EnsureRequired_配置齐全_不抛异常()
    {
        var config = Complete();
        ConfigurationValidator.EnsureRequired(config);
    }

    [Fact]
    public void EnsureRequired_缺连接串_抛异常且消息含缺失键()
    {
        var config = Complete();
        config.Remove("ConnectionStrings:Default");

        var ex = Assert.Throws<ConfigSourceUnavailableException>(
            () => ConfigurationValidator.EnsureRequired(config));
        Assert.Contains("ConnectionStrings:Default", ex.Message);
    }

    [Fact]
    public void EnsureRequired_值为空白_等同缺失()
    {
        var config = Complete();
        config["Redis:ConnectionString"] = "   ";

        var ex = Assert.Throws<ConfigSourceUnavailableException>(
            () => ConfigurationValidator.EnsureRequired(config));
        Assert.Contains("Redis:ConnectionString", ex.Message);
    }

    [Fact]
    public void EnsureRequired_服务追加的键缺失_一并报出()
    {
        var config = Complete();
        var extra = new List<string> { "Jwt:Secret" };

        var ex = Assert.Throws<ConfigSourceUnavailableException>(
            () => ConfigurationValidator.EnsureRequired(config, extra));
        Assert.Contains("Jwt:Secret", ex.Message);
    }

    [Fact]
    public void EnsureRequired_键名大小写不敏感()
    {
        var config = Complete();
        config.Remove("Consul:Address");
        config["consul:address"] = "http://127.0.0.1:8500";

        ConfigurationValidator.EnsureRequired(config);
    }

    [Fact]
    public void EnsureRequired_重复的键_只报一次()
    {
        var config = Complete();
        config.Remove("Redis:ConnectionString");
        var extra = new List<string> { "Redis:ConnectionString" };

        var ex = Assert.Throws<ConfigSourceUnavailableException>(
            () => ConfigurationValidator.EnsureRequired(config, extra));
        Assert.Equal(1, ex.Message.Split("Redis:ConnectionString").Length - 1);
    }
}

