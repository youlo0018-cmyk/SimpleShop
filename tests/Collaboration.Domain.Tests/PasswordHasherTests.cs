using Collaboration.Domain.Security;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>密码哈希单元测试。前台与后台账号共用本实现（DATA_SPEC 5.1）。</summary>
public class PasswordHasherTests
{
    [Fact]
    public void Hash_格式为算法_次数_盐_哈希四段()
    {
        var parts = PasswordHasher.Hash("Test123456").Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2", parts[0]);
        Assert.Equal(210000, int.Parse(parts[1]));
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);
    }

    [Fact]
    public void Hash_同一密码两次_结果不同_证明是随机盐()
    {
        var a = PasswordHasher.Hash("Test123456").Split('$');
        var b = PasswordHasher.Hash("Test123456").Split('$');
        Assert.NotEqual(a[2], b[2]);
        Assert.NotEqual(a[3], b[3]);
    }

    [Fact]
    public void Hash_明文不出现在结果中()
    {
        Assert.DoesNotContain("Test123456", PasswordHasher.Hash("Test123456"));
    }

    [Fact]
    public void Verify_正确密码通过_错误密码不通过()
    {
        var stored = PasswordHasher.Hash("Test123456");
        Assert.True(PasswordHasher.Verify("Test123456", stored));
        Assert.False(PasswordHasher.Verify("Test1234567", stored));
        Assert.False(PasswordHasher.Verify("test123456", stored));
        Assert.False(PasswordHasher.Verify("", stored));
    }

    [Fact]
    public void Verify_哈希格式非法_返回false不抛异常()
    {
        Assert.False(PasswordHasher.Verify("Test123456", "not-a-hash"));
        Assert.False(PasswordHasher.Verify("Test123456", "pbkdf2$xxx"));
        Assert.False(PasswordHasher.Verify("Test123456", "md5$1$aaa$bbb"));
        Assert.False(PasswordHasher.Verify("Test123456", ""));
    }

    [Fact]
    public void Verify_同密码不同用户_各自用自己的盐互不影响()
    {
        var alice = PasswordHasher.Hash("SamePass123");
        var bob = PasswordHasher.Hash("SamePass123");
        Assert.True(PasswordHasher.Verify("SamePass123", alice));
        Assert.True(PasswordHasher.Verify("SamePass123", bob));
        Assert.False(PasswordHasher.Verify("OtherPass123", alice));
        Assert.False(PasswordHasher.Verify("OtherPass123", bob));
    }

    [Fact]
    public void Hash_空密码_抛异常()
    {
        Assert.Throws<ArgumentException>(() => PasswordHasher.Hash(string.Empty));
        Assert.Throws<ArgumentNullException>(() => PasswordHasher.Hash(null!));
    }

    [Fact]
    public void 存储串为ASCII安全字符()
    {
        var stored = PasswordHasher.Hash("密码带中文 Test123456");
        Assert.All(stored, c => Assert.True(c < 128, "存储串应为 ASCII 安全字符"));
    }
}

