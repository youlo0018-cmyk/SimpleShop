using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace CustomerService.Domain.Entities;

/// <summary>前台客户账号。与后台账号完全隔离，不同库、不同登录端点、不同令牌（DATA_SPEC 2.6）。</summary>
public class Customer : EntityBase
{
    /// <summary>登录名，全局唯一。</summary>
    [Column(Name = "customer_name", StringLength = 64)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>密码哈希。只存哈希，禁止存明文。</summary>
    [Column(Name = "password_hash", StringLength = 256)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>手机号，全局唯一。</summary>
    [Column(Name = "phone", StringLength = 20)]
    public string Phone { get; set; } = string.Empty;

    /// <summary>昵称，C 端展示用。</summary>
    [Column(Name = "nick_name", StringLength = 64)]
    public string NickName { get; set; } = string.Empty;

    /// <summary>头像 URL。</summary>
    [Column(Name = "avatar", StringLength = 512)]
    public string Avatar { get; set; } = string.Empty;

    /// <summary>性别。0 未知 / 1 男 / 2 女。</summary>
    [Column(Name = "gender")]
    public int Gender { get; set; }

    /// <summary>生日。</summary>
    [Column(Name = "birthday")]
    public DateTime? Birthday { get; set; }

    /// <summary>最后登录时间，UTC。</summary>
    [Column(Name = "last_login_at")]
    public DateTime? LastLoginAt { get; set; }
}

