using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Gateway.Api;

/// <summary>令牌识别结果。</summary>
/// <param name="Succeeded">是否校验通过。</param>
/// <param name="Principal">通过时的身份；失败时为 null。</param>
/// <param name="Kind">令牌类型（后台 / 客户）；失败时为 null。</param>
/// <param name="Error">失败原因，面向日志（不要回给客户端）。</param>
public readonly record struct TokenValidationOutcome(
    bool Succeeded,
    ClaimsPrincipal? Principal,
    string? Kind,
    string? Error);

/// <summary>双令牌验签：后台 RS256 + 客户 HS256。</summary>
/// <remarks>
/// <para>两套令牌的差异只在签名方式与签发方，其余声明形状一致，所以网关把它们归一成同一个
/// ClaimsPrincipal 往下游传——下游不需要知道自己收到的是后台令牌还是客户令牌，
/// 它只看 tenant_type / platform_id / permission 这些声明（BUSINESS 4.2）。</para>
///
/// <para><b>按 JWT 头里的 alg 分派，而不是两种都试一遍</b>。两种都试会让「用 HS256 签一个
/// alg:RS256 的令牌」这类混淆攻击有机会蒙混过关，也白白多一次密钥运算。</para>
/// </remarks>
public sealed class DualTokenValidator
{
    private readonly TokenValidationParameters _adminParameters;
    private readonly TokenValidationParameters _customerParameters;
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>令牌类型：后台。</summary>
    public const string KindAdmin = "admin";

    /// <summary>令牌类型：客户。</summary>
    public const string KindCustomer = "customer";

    /// <summary>构造验签器。</summary>
    /// <param name="options">网关配置。</param>
    /// <remarks>
    /// 证书缺失直接抛异常让服务起不来：网关起不来比「网关起来了但不验签」安全得多。
    /// 后者会让所有人绕过鉴权，而且日志里看不出任何异常。
    /// </remarks>
    public DualTokenValidator(IOptions<GatewayOptions> options)
    {
        var opt = options.Value;

        if (string.IsNullOrWhiteSpace(opt.AdminToken.SigningCertificatePath))
        {
            throw new InvalidOperationException(
                "缺少配置 Gateway:AdminToken:SigningCertificatePath。" +
                "网关需要 AuthService 的证书公钥来验签后台令牌，请先运行 ./scripts/generate-signing-cert.ps1，" +
                "并确认它与 Auth:SigningCertificatePath 指向同一个文件。");
        }

        var path = Path.GetFullPath(opt.AdminToken.SigningCertificatePath);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"签名证书不存在: {path}");
        }

        // 只取公钥：网关只需要验签，不该具备签发能力。
        var cert = X509CertificateLoader.LoadPkcs12FromFile(
            path, password: null, X509KeyStorageFlags.EphemeralKeySet);
        var rsa = cert.GetRSAPublicKey()
            ?? throw new InvalidOperationException($"证书 {path} 不是 RSA 证书，无法用于 RS256 验签。");

        _adminParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = opt.AdminToken.Issuer,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            // 显式钉死 RS256：只接受这一种算法，杜绝 alg=none 与 RS→HS 降级
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKey = new RsaSecurityKey(rsa)
        };

        if (string.IsNullOrWhiteSpace(opt.CustomerToken.Secret))
        {
            throw new InvalidOperationException("缺少配置 Gateway:CustomerToken:Secret，网关无法验签客户令牌。");
        }

        _customerParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = opt.CustomerToken.Issuer,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            IssuerSigningKey = new SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes(opt.CustomerToken.Secret))
        };
    }

    /// <summary>从 Authorization 头里取出 Bearer 令牌。</summary>
    /// <param name="authorization">Authorization 头的值。</param>
    /// <returns>令牌原文；不是 Bearer 形式时返回 null。</returns>
    public static string? ExtractBearer(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization)) return null;

        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var token = authorization[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>按 alg 分派并验签。</summary>
    /// <param name="token">JWT 原文。</param>
    /// <returns>校验结果。</returns>
    public async Task<TokenValidationOutcome> ValidateAsync(string token)
    {
        JsonWebToken unvalidated;
        try
        {
            unvalidated = _handler.ReadJsonWebToken(token);
        }
        catch (Exception ex)
        {
            return new TokenValidationOutcome(false, null, null, $"无法解析 JWT: {ex.Message}");
        }

        var alg = unvalidated.Alg;

        // 按 alg 分派，而不是两种密钥都试一遍。
        // 两种都试会给「用 HS256 密钥签一个 alg 写成 RS256 的令牌」留下蒙混过关的机会，
        // 也白白多一次密钥运算。其他算法（含 alg=none）一律拒绝。
        TokenValidationParameters? parameters;
        string? kind;

        switch (alg)
        {
            case SecurityAlgorithms.RsaSha256:
                parameters = _adminParameters;
                kind = KindAdmin;
                break;

            case SecurityAlgorithms.HmacSha256:
                parameters = _customerParameters;
                kind = KindCustomer;
                break;

            default:
                return new TokenValidationOutcome(false, null, null, $"不支持的签名算法: {alg}");
        }

        var result = await _handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid)
        {
            return new TokenValidationOutcome(false, null, kind, $"验签失败: {result.Exception?.Message}");
        }

        // ValidateTokenAsync 返回的是 IDictionary<string, object>，**同名多次出现的声明会变成数组**
        // （permission 就是这种：令牌里 78 个权限点会是一个长度为 78 的数组）。
        // 必须把数组摊平成多条 Claim——直接 ToString() 的话下游只能看到一个 "[user:read, ...]" 的长字符串，
        // RBAC 判定全部失效。这是必须写在这里的摊平逻辑，不是可以省的便利代码。
        var claims = new List<Claim>();
        foreach (var (type, value) in result.Claims)
        {
            foreach (var item in Flatten(value))
            {
                claims.Add(new Claim(type, item));
            }
        }

        var identity = new ClaimsIdentity(claims, kind, ClaimTypes.Name, ClaimTypes.Role);
        return new TokenValidationOutcome(true, new ClaimsPrincipal(identity), kind, null);
    }

    /// <summary>把验签结果里的声明值摊平成字符串列表（数组逐项展开）。</summary>
    /// <param name="value">声明的原始值，可能是字符串、数组或 JsonElement。</param>
    /// <returns>摊平后的字符串序列。</returns>
    private static IEnumerable<string> Flatten(object? value) => value switch
    {
        null => [],
        string s => [s],
        JsonElement element when element.ValueKind == JsonValueKind.Array
            => element.EnumerateArray().SelectMany(x => Flatten(x)),
        JsonElement element => [element.ToString()],
        IEnumerable<object> list => list.SelectMany(Flatten),
        _ => [value.ToString() ?? string.Empty]
    };
}
