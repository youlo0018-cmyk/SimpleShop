using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace UserService.Domain.Entities;

/// <summary>后台账号（平台 / 商户 / 超管）。与前台客户账号完全隔离（DATA_SPEC 2.4、2.5）。</summary>
/// <remarks>
/// 继承 EntityBase 而**不是** AdminEntityBase：
/// - 从 EntityBase 拿到雪花 Id、CreatedAt/UpdatedAt、软删标记与软删自动过滤（不用手写）。
/// - 不继承 AdminEntityBase 就**不会**被注入租户过滤——后台账号本身就是租户身份载体，
///   查后台账号要按调用者身份显式裁剪（见 UserService 的 QueryPagedAsync 参数），不能被隐式过滤覆盖。
/// - 租户字段（TenantType / PlatformId / MerchantId）在本类自己声明，不重复继承。
/// 真实角色绑定在权限中心 user_role，本表**不得**有任何角色字段兜底（fail-closed，BUSINESS 5.3）。
/// </remarks>
[Table(Name = "app_user")]
public class User : EntityBase
{
    /// <summary>登录名，全局唯一。</summary>
    [Column(Name = "user_name", StringLength = 64)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>密码哈希。只存哈希，禁止明文；用 Collaboration 的 PasswordHasher（PBKDF2 + 随机盐）。</summary>
    [Column(Name = "password_hash", StringLength = 256)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>手机号，全局唯一。</summary>
    [Column(Name = "phone", StringLength = 20)]
    public string Phone { get; set; } = string.Empty;

    /// <summary>邮箱，可空。</summary>
    [Column(Name = "email", StringLength = 128)]
    public string Email { get; set; } = string.Empty;

    /// <summary>昵称。</summary>
    [Column(Name = "nick_name", StringLength = 64)]
    public string NickName { get; set; } = string.Empty;

    /// <summary>头像 URL。</summary>
    [Column(Name = "avatar", StringLength = 512)]
    public string Avatar { get; set; } = string.Empty;

    /// <summary>租户类型。1 平台 / 2 商户。禁止 customer——客户账号在 CustomerService，与本表完全隔离。</summary>
    [Column(Name = "tenant_type")]
    public int TenantType { get; set; }

    /// <summary>所属平台 Id。0 表示超管（平台级，不受平台限制）。</summary>
    [Column(Name = "platform_id")]
    public long PlatformId { get; set; }

    /// <summary>所属商户 Id。平台账号为 0。</summary>
    [Column(Name = "merchant_id")]
    public long MerchantId { get; set; }

    /// <summary>状态。1 启用 / 2 停用。停用只挡新登录，已签发令牌仍有效到过期（REVIEW P2 风险 18）。</summary>
    [Column(Name = "status")]
    public int Status { get; set; } = 1;

    /// <summary>最后登录时间，UTC。</summary>
    [Column(Name = "last_login_at")]
    public DateTime? LastLoginAt { get; set; }
}