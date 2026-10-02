namespace AuthService.Domain;

/// <summary>后台令牌的声明名。网关按这些名字解析并转成 X-Claim-* 请求头。</summary>
/// <remarks>
/// 与 BUSINESS.md 5.3 的令牌内容约定一致：tenant_type / platform_id / merchant_id /
/// permission×N / role×N。网关只认这几个名字，改名要同步改网关。
/// </remarks>
public static class AdminTokenClaims
{
    /// <summary>主体标识，后台为 User.Id。用标准的 sub。</summary>
    public const string Subject = "sub";

    /// <summary>登录名，后台界面显示用。</summary>
    public const string UserName = "user_name";

    /// <summary>昵称，后台界面显示用。</summary>
    public const string NickName = "nick_name";

    /// <summary>租户类型。1 平台 / 2 商户。</summary>
    public const string TenantType = "tenant_type";

    /// <summary>平台 Id，超管为 0。</summary>
    public const string PlatformId = "platform_id";

    /// <summary>商户 Id，平台账号为 0。</summary>
    public const string MerchantId = "merchant_id";

    /// <summary>权限点编码，JWT 数组声明。</summary>
    public const string Permission = "permission";

    /// <summary>角色编码，JWT 数组声明。</summary>
    public const string Role = "role";
}

/// <summary>后台账号的租户类型。与 UserService 的 TenantTypes 保持一致。</summary>
public static class AdminTenantTypes
{
    /// <summary>平台账号（含超管，超管的 platform_id 为 0）。</summary>
    public const int Platform = 1;

    /// <summary>商户账号。</summary>
    public const int Merchant = 2;
}