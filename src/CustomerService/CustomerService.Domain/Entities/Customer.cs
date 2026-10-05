using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace CustomerService.Domain.Entities;

/// <summary>前台客户账号。与后台账号完全隔离，不同库、不同登录端点、不同令牌（DATA_SPEC 2.6）。</summary>
/// <remarks>
/// 表名列名一律显式指定小写下划线，与 deploy/sql 的 DDL 对齐（DATA_SPEC 2.7）。
/// 不加 [Table]，FreeSql 会按实体名去找带引号的 "Customer"，而实际建的是小写 customer，
/// 报 42P01 relation does not exist。同理每个属性都要有 [Column(Name=...)]。
/// </remarks>
[Table(Name = "customer")]
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

    /// <summary>账号状态，见 <see cref="CustomerStatuses"/>。</summary>
    /// <remarks>
    /// 停用后<b>禁止登录</b>。这一列是后加的：权限点 `customer:status` 早就种好了，
    /// 但库里没有地方存状态，运营点「停用」无处落库，等于这个功能不存在。
    /// </remarks>
    [Column(Name = "status")]
    public int Status { get; set; } = CustomerStatuses.Enabled;
}

/// <summary>客户账号状态。</summary>
public static class CustomerStatuses
{
    /// <summary>正常，可登录。</summary>
    public const int Enabled = 1;

    /// <summary>停用，禁止登录。</summary>
    public const int Disabled = 2;

    /// <summary>状态中文名（后台展示用，不下发数字枚举）。</summary>
    /// <param name="status">状态值。</param>
    /// <returns>中文名。</returns>
    public static string NameOf(int status)
        => status == Disabled ? "已停用" : "正常";
}

