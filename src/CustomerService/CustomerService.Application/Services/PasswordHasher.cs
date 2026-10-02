using System.Security.Cryptography;

namespace CustomerService.Application.Services;

/// <summary>密码哈希：PBKDF2-SHA256 + 随机盐。</summary>
/// <remarks>
/// 存在的理由：CODING_STANDARD 只要求「只存哈希」，没指定算法。这里直接用 .NET 内置的
/// Rfc2898DeriveBytes，不引入 Microsoft.AspNetCore.Identity，避免把 ASP.NET 依赖带进 Application 层。
/// 存储格式：pbkdf2$迭代次数$盐Base64$哈希Base64。迭代次数随哈希一起存，
/// 便于日后提高参数并仍然兼容旧值。
/// </remarks>
public static class PasswordHasher
{
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string Prefix = "pbkdf2";

    /// <summary>生成密码哈希。</summary>
    /// <param name="password">明文密码。不会以任何形式返回或记录。</param>
    /// <returns>可直接存入 password_hash 列的字符串。</returns>
    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>校验密码。</summary>
    /// <param name="password">待校验的明文密码。</param>
    /// <param name="stored">数据库中存的哈希。</param>
    /// <returns>匹配返回 true。哈希格式不合法也返回 false，不抛异常。</returns>
    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored)) return false;
        if (!stored.StartsWith(Prefix + "$", StringComparison.Ordinal)) return false;

        var parts = stored.Split('$');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

