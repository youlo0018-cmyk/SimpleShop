using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AuthService.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthService.Infrastructure;

/// <summary>加载 RS256 签名证书；本地开发时若文件不存在则自动生成一份。</summary>
/// <remarks>
/// 证书本身不入库（.gitignore 忽略 *.pfx）：私钥进 git 就等于把令牌签发权一起提交了。
/// 生成逻辑放在运行期而不是只在脚本里，是为了「clone 下来就能跑」；
/// 但只要文件已存在就绝不会覆盖——覆盖会让所有已签发令牌立刻验签失败，把在线用户全踢下线。
/// </remarks>
public sealed class SigningCertificateProvider
{
    private readonly AuthOptions _options;
    private readonly ILogger<SigningCertificateProvider> _logger;

    /// <summary>构造证书提供者。</summary>
    /// <param name="options">认证中心配置。</param>
    /// <param name="logger">日志器。</param>
    public SigningCertificateProvider(IOptions<AuthOptions> options, ILogger<SigningCertificateProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>取签名证书，必要时生成。</summary>
    /// <returns>含私钥的 X509 证书。</returns>
    /// <exception cref="InvalidOperationException">未配置证书路径时抛出。</exception>
    public X509Certificate2 GetOrCreate()
    {
        if (string.IsNullOrWhiteSpace(_options.SigningCertificatePath))
        {
            throw new InvalidOperationException(
                "缺少配置 Auth:SigningCertificatePath。请在 AgileConfig 补上，" +
                "并执行 ./scripts/generate-signing-cert.ps1 生成证书。");
        }

        var path = Path.GetFullPath(_options.SigningCertificatePath);

        if (File.Exists(path))
        {
            _logger.LogInformation("加载已有签名证书 {Path}", path);
            // 注意：必须带导出私钥选项读回来，否则 OpenIddict 拿不到 RS256 私钥
            return X509CertificateLoader.LoadPkcs12FromFile(
                path, password: null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            "CN=SimpleShop Auth Signing",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, critical: false)); // serverAuth

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));

        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));

        _logger.LogWarning(
            "签名证书不存在，已在 {Path} 生成一份自签证书（有效期 5 年）。" +
            "生产环境应由部署脚本注入，且 AuthService 与 Gateway 指向同一份文件。",
            path);

        return X509CertificateLoader.LoadPkcs12FromFile(
            path, password: null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }
}