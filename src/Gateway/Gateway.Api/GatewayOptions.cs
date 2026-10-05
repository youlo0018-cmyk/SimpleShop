namespace Gateway.Api;

/// <summary>网关配置，对应 AgileConfig 的 Gateway 节。</summary>
public sealed class GatewayOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "Gateway";

    /// <summary>与各下游服务共用的内部共享口令。转成 X-Internal-Token 注入下游。</summary>
    /// <remarks>
    /// 下游的 TenantContextMiddleware **只认这个口令**才采信 X-Claim-*。
    /// 网关是唯一合法的写入方——因此入站请求里同名的头必须先被剥掉（见 GatewaySecurityMiddleware）。
    /// </remarks>
    public string InternalToken { get; set; } = string.Empty;

    /// <summary>后台令牌（RS256）配置。</summary>
    public AdminTokenOptions AdminToken { get; set; } = new();

    /// <summary>客户令牌（HS256）配置。</summary>
    public CustomerTokenOptions CustomerToken { get; set; } = new();

    /// <summary>RBAC 配置。</summary>
    public RbacOptions Rbac { get; set; } = new();

    /// <summary>无需登录与权限校验的路径前缀（精确路径列表）。</summary>
    /// <remarks>
    /// 登记 / 登录 / 换令牌 / 图片回源。**只列白名单，不列黑名单**——
    /// 写黑名单意味着「忘了加的接口会默认放行」，那等于没做鉴权。
    /// </remarks>
    public string[] AnonymousPaths { get; set; } =
    [
        "/gateway/customers/Register",
        "/gateway/customers/Login",
        "/gateway/auth/Token",
        "/gateway/shop/products/List",
        "/gateway/shop/products/Detail",
        "/gateway/shop/catalog/CategoryTree",
        "/gateway/shop/catalog/Brands",
        "/gateway/design/Store",
        "/gateway/design/PlatformStore",
        "/gateway/evaluates/List",
        "/gateway/marketing/seckill/sessions/Public",
        "/gateway/coupons/Available"
    ];

    /// <summary>无需登录、但需要 RBAC 校验的路径前缀（登录后才能访问、只看公共数据）。</summary>
    public string[] AnonymousPathPrefixes { get; set; } = ["/gateway/files/Content/"];
}

/// <summary>后台令牌（RS256）配置。</summary>
public sealed class AdminTokenOptions
{
    /// <summary>必须匹配的签发方。</summary>
    public string Issuer { get; set; } = "https://simpleshop.local/";

    /// <summary>RS256 签名证书路径。<b>必须与 AuthService 指向同一份文件</b>，网关取其公钥验签。</summary>
    public string SigningCertificatePath { get; set; } = string.Empty;
}

/// <summary>客户令牌（HS256）配置。</summary>
public sealed class CustomerTokenOptions
{
    /// <summary>必须匹配的签发方。</summary>
    public string Issuer { get; set; } = "simpleshop-customer";

    /// <summary>HS256 共享密钥。与 CustomerService 签发时用的是同一个值。</summary>
    /// <remarks>至少 32 字节；短密钥在 HS256 下等于形同虚设，会被离线爆破。</remarks>
    public string Secret { get; set; } = string.Empty;
}

/// <summary>网关 RBAC 配置。</summary>
public sealed class RbacOptions
{
    /// <summary>权限中心地址，用于拉「路径 → 权限点」映射。</summary>
    public string PermissionServiceUrl { get; set; } = string.Empty;

    /// <summary>映射缓存秒数。默认 30 秒，与 BUSINESS.md 5.3 一致。</summary>
    public int CacheSeconds { get; set; } = 30;

    /// <summary>
    /// 拉不到映射时的行为。
    /// </summary>
    /// <remarks>
    /// true = 全部放行（不可用，等于没有鉴权）；false = 全部拒绝（可用性换安全）。
    /// 默认 false：宁可服务不可用，也不能让鉴权静默失效——
    /// 那会让任何人在权限中心挂掉时拿到全站权限。
    /// </remarks>
    public bool AllowAllWhenUnavailable { get; set; }
}
