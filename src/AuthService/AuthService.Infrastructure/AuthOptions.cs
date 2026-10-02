namespace AuthService.Infrastructure;

/// <summary>认证中心配置，对应 AgileConfig 的 Auth 节。</summary>
public sealed class AuthOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "Auth";

    /// <summary>令牌签发方（iss），也是网关校验时必须匹配的签发方。</summary>
    public string Issuer { get; set; } = "simpleshop";

    /// <summary>令牌受众（aud）。</summary>
    public string Audience { get; set; } = "simpleshop-admin";

    /// <summary>后台公开客户端 Id。公开客户端没有密钥，用 password flow 换令牌。</summary>
    public string ClientId { get; set; } = "admin-app";

    /// <summary>访问令牌有效期（小时）。</summary>
    public int AccessTokenHours { get; set; } = 2;

    /// <summary>刷新令牌有效期（天）。</summary>
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>
    /// RS256 签名证书的 pfx 文件路径。
    /// </summary>
    /// <remarks>
    /// <b>AuthService 与 Gateway 必须读同一个文件</b>（DATA_SPEC 1.5）：
    /// 签发方用私钥签名，验签方只需要公钥，但两边引用同一个 pfx 最不容易搞混。
    /// 证书不入库（.gitignore 已忽略 *.pfx），由脚本在部署时生成。
    /// </remarks>
    public string SigningCertificatePath { get; set; } = string.Empty;
}