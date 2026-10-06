namespace Collaboration.Domain.Context;

/// <summary>
/// 租户上下文：当前请求的租户身份与操作人。
/// </summary>
/// <remarks>
/// 链路位置：Api 层从网关注入的 X-Claim-* 请求头构造。下游服务只信任网关，不读客户端自传的租户字段。
/// AOP 的租户过滤、创建人与操作人字段填充都读本对象。作用域为每个请求一个（Scoped）。
/// 依据：DATA_SPEC.md 3.2、BUSINESS.md 1.3。
/// </remarks>
public sealed class TenantContext
{
    /// <summary>访问上下文，决定 AOP 注入哪些过滤条件。</summary>
    public AccessContext Access { get; set; } = AccessContext.Anonymous;

    /// <summary>租户类型。1 平台，2 商户，3 客户。与令牌声明 tenant_type 一致。</summary>
    public int TenantType { get; set; }

    /// <summary>
    /// 当前平台 Id，雪花 Id。
    /// </summary>
    /// <remarks>超管为 0，表示不受平台限制；C 端为 0，不按平台裁剪，改由可见性过滤兜底。</remarks>
    public long PlatformId { get; set; }

    /// <summary>当前商户 Id，雪花 Id。平台账号与 C 端为 0。</summary>
    public long MerchantId { get; set; }

    /// <summary>当前操作人 Id，雪花 Id。后台为 User.Id，C 端为 Customer.Id，游客为 0。</summary>
    public long UserId { get; set; }

    /// <summary>当前操作人姓名，用于写入创建人与操作人快照。匿名时为空字符串。</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>客户唯一编码；后台与游客为空字符串。</summary>
    public string CustomerNo { get; set; } = string.Empty;

    /// <summary>当前权限点集合，由令牌解析。权限只认显式绑定，无绑定即无权限（fail-closed）。</summary>
    public IReadOnlyList<string> Permissions { get; set; } = Array.Empty<string>();

    /// <summary>是否为平台超管（不受平台限制）。</summary>
    public bool IsSuperAdmin => Access == AccessContext.Admin && TenantType == 1 && PlatformId == 0;

    /// <summary>是否为平台账号（只看本平台）。</summary>
    public bool IsPlatform => Access == AccessContext.Admin && TenantType == 1 && PlatformId > 0;

    /// <summary>是否为商户账号（只看本商户）。</summary>
    public bool IsMerchant => Access == AccessContext.Admin && TenantType == 2;

    /// <summary>是否为已登录客户。</summary>
    public bool IsCustomer => Access == AccessContext.Customer;

    /// <summary>是否为游客（未登录）。</summary>
    public bool IsAnonymous => Access == AccessContext.Anonymous;

    /// <summary>是否为服务间内部调用（<c>/internal</c> 前缀）。</summary>
    public bool IsInternal => Access == AccessContext.Internal;

    /// <summary>
    /// 当前访问上下文是否需要注入公开可见性过滤。
    /// </summary>
    /// <remarks>C 端与游客需要；后台不需要。这是 AOP 判断可见性过滤器是否生效的唯一依据。</remarks>
    public bool ShouldFilterPublicVisibility
        => Access is AccessContext.Customer or AccessContext.Anonymous;

    /// <summary>
    /// 判断当前身份是否拥有指定权限点。
    /// </summary>
    /// <param name="permissionCode">权限编码，如 order:ship。</param>
    /// <returns>显式绑定则返回 true；无绑定返回 false（fail-closed，不用账号字段兜底）。</returns>
    public bool HasPermission(string permissionCode)
        => !string.IsNullOrEmpty(permissionCode) && Permissions.Contains(permissionCode);
}

