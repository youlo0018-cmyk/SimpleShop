using Collaboration.Domain.Common;
using Collaboration.Domain.Validation;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace UserService.Application.Features.User.ManageUser;

/// <summary>租户类型。1 平台 / 2 商户。<b>没有 customer</b>——客户账号属于 CustomerService，与后台账号域隔离。</summary>
public static class TenantTypes
{
    /// <summary>平台账号。</summary>
    public const int Platform = 1;

    /// <summary>商户账号。</summary>
    public const int Merchant = 2;
}

/// <summary>新建后台账号。</summary>
/// <remarks>字段清单与 DATA_SPEC 5.18 一一对应，不多不少。</remarks>
/// <param name="UserName">登录名，3-64 字符，全局唯一。</param>
/// <param name="Password">明文密码，至少 8 位且含字母与数字；只传哈希入库。</param>
/// <param name="Phone">手机号，全局唯一。</param>
/// <param name="TenantType">租户类型，1 平台 / 2 商户，禁止客户类型。</param>
/// <param name="NickName">昵称。</param>
/// <param name="Email">邮箱，可空。</param>
/// <param name="Avatar">头像地址，可空。</param>
/// <param name="PlatformId">所属平台，平台账号必填（超管填 0）。</param>
/// <param name="MerchantId">所属商户，商户账号必填。</param>
/// <param name="RoleIds">角色 Id 集合，至少 1 个。</param>
/// <param name="Status">状态，1 启用 / 2 停用，默认启用。</param>
public record CreateUserCommand(
    string UserName,
    string Password,
    string Phone,
    int TenantType,
    string NickName,
    string Email = "",
    string Avatar = "",
    long PlatformId = 0,
    long MerchantId = 0,
    IReadOnlyCollection<long>? RoleIds = null,
    int Status = 1) : IRequest<ApiResponse<long>>;

/// <summary>编辑后台账号。<b>不允许改租户类型与所属平台 / 商户</b>——租户身份变更走「停用旧号 + 新建」。</summary>
/// <remarks>字段清单与 DATA_SPEC 5.19 一一对应：传了才改，没传保持原值（RoleIds 传了就全量覆盖）。</remarks>
/// <param name="UserId">账号 Id。</param>
/// <param name="UserName">新登录名，null 表示不改；改后仍需全局唯一。</param>
/// <param name="Phone">新手机号，null 表示不改；改后仍需全局唯一。</param>
/// <param name="NickName">新昵称，null 表示不改。</param>
/// <param name="Email">新邮箱，null 表示不改。</param>
/// <param name="Avatar">新头像，null 表示不改。</param>
/// <param name="Status">新状态，null 表示不改。</param>
/// <param name="RoleIds">新角色集合，null 表示不改；传了就重建全部绑定。</param>
public record UpdateUserCommand(
    long UserId,
    string? UserName = null,
    string? Phone = null,
    string? NickName = null,
    string? Email = null,
    string? Avatar = null,
    int? Status = null,
    IReadOnlyCollection<long>? RoleIds = null) : IRequest<ApiResponse>;

/// <summary>重置密码。后台直接设置新密码，不需要旧密码。</summary>
public record ResetPasswordCommand(long UserId, string NewPassword) : IRequest<ApiResponse>;

/// <summary>启用 / 停用账号。停用只挡新登录，已签发令牌仍有效到过期。</summary>
public record ChangeUserStatusCommand(long UserId, int Status) : IRequest<ApiResponse>;

/// <summary>分页查询后台账号。</summary>
public record QueryUsersCommand(
    int Page = 1,
    int PageSize = 20,
    string Keyword = "",
    long PlatformId = 0,
    long MerchantId = 0,
    int Status = 0) : IRequest<ApiResponse<List<UserListItem>>>;

/// <summary>账号列表项。含所属平台 / 商户名称，前端直接展示不再二次查询（DATA_SPEC 4.3）。</summary>
public record UserListItem(
    string Id,
    string UserName,
    string NickName,
    string Phone,
    string Email,
    string Avatar,
    string TenantTypeText,
    string PlatformName,
    string MerchantName,
    int Status,
    string LastLoginAt);

/// <summary>后台账号命令的校验器。</summary>
public static class UserValidators
{
    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddUserValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<CreateUserCommand>, CreateUserValidator>();
        services.AddScoped<IValidator<UpdateUserCommand>, UpdateUserValidator>();
        services.AddScoped<IValidator<ResetPasswordCommand>, ResetPasswordValidator>();
        services.AddScoped<IValidator<ChangeUserStatusCommand>, ChangeUserStatusValidator>();
        services.AddScoped<IValidator<QueryUsersCommand>, QueryUsersValidator>();
    }

    /// <summary>密码强度校验：至少 8 位且含字母与数字（DATA_SPEC 5.1）。</summary>
    /// <param name="password">明文密码。</param>
    /// <returns>强度不足返回 false。</returns>
    public static bool IsWeakPassword(string password)
        => string.IsNullOrEmpty(password)
           || password.Length < 8
           || !password.Any(char.IsLetter)
           || !password.Any(char.IsDigit);

    private sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
    {
        public CreateUserValidator()
        {
            RuleFor(x => x.UserName).NotEmpty().Length(3, 64).WithMessage("登录名必须为 3-64 个字符");
            RuleFor(x => x.Password).NotEmpty()
                .Must(p => !IsWeakPassword(p)).WithMessage(ValidationPatterns.passwordMessage);
            RuleFor(x => x.Phone).NotEmpty().Matches(ValidationPatterns.phonePattern)
                .WithMessage(ValidationPatterns.phoneMessage);
            // 只允许 1 平台 / 2 商户，从入口就挡住 customer（BUSINESS 账号域互斥）
            RuleFor(x => x.TenantType).Must(t => t is TenantTypes.Platform or TenantTypes.Merchant)
                .WithMessage("租户类型只能是 1 平台 或 2 商户，禁止客户类型");
            RuleFor(x => x.NickName).NotEmpty().Length(1, 64).WithMessage("昵称必须为 1-64 个字符");
            RuleFor(x => x.Email).Matches(ValidationPatterns.emailPattern)
                .When(x => !string.IsNullOrWhiteSpace(x.Email)).WithMessage(ValidationPatterns.emailMessage);
            RuleFor(x => x.Avatar).MaximumLength(512).WithMessage("头像地址最多 512 个字符");
            RuleFor(x => x.MerchantId).GreaterThan(0)
                .When(x => x.TenantType == TenantTypes.Merchant).WithMessage("商户账号必须指定所属商户");
            // 平台账号必须落在某个平台上：超管传 0，其余平台账号传本平台 Id。
            // 「只有超管能建 platformId = 0」这半条要读租户上下文，放在 Handler 里判（校验器拿不到身份）。
            RuleFor(x => x.PlatformId).GreaterThanOrEqualTo(0)
                .When(x => x.TenantType == TenantTypes.Platform).WithMessage("平台账号必须指定所属平台（超管填 0）");
            // 至少 1 个角色：不选角色等于建了一个登录后什么都做不了的账号（DATA_SPEC 5.18 fail-closed）。
            // 写成单条 Must 而不是 NotNull().Must()：链式两条会对同一个 null 值报两次同样的错。
            RuleFor(x => x.RoleIds).Must(ids => ids is { Count: > 0 }).WithMessage("必须至少选择一个角色");
            RuleFor(x => x.Status).Must(s => s is 1 or 2).WithMessage("状态只能是 1 启用 或 2 停用");
        }
    }

    private sealed class UpdateUserValidator : AbstractValidator<UpdateUserCommand>
    {
        public UpdateUserValidator()
        {
            RuleFor(x => x.UserId).GreaterThan(0).WithMessage("账号 Id 不合法");
            RuleFor(x => x.UserName).Length(3, 64)
                .When(x => x.UserName is not null).WithMessage("登录名必须为 3-64 个字符");
            RuleFor(x => x.Phone).Matches(ValidationPatterns.phonePattern)
                .When(x => x.Phone is not null).WithMessage(ValidationPatterns.phoneMessage);
            RuleFor(x => x.NickName).Length(1, 64)
                .When(x => x.NickName is not null).WithMessage("昵称必须为 1-64 个字符");
            RuleFor(x => x.Email).Matches(ValidationPatterns.emailPattern)
                .When(x => !string.IsNullOrWhiteSpace(x.Email)).WithMessage(ValidationPatterns.emailMessage);
            RuleFor(x => x.Avatar).MaximumLength(512)
                .When(x => x.Avatar is not null).WithMessage("头像地址最多 512 个字符");
            RuleFor(x => x.Status).Must(s => s is 1 or 2)
                .When(x => x.Status.HasValue).WithMessage("状态只能是 1 启用 或 2 停用");
            // RoleIds 传 null = 不改绑定；传了（哪怕是空数组）就是全量覆盖，所以至少要 1 个
            RuleFor(x => x.RoleIds).Must(ids => ids is { Count: > 0 })
                .When(x => x.RoleIds is not null).WithMessage("角色至少要选 1 个（解绑全部请用停用账号）");
        }
    }

    private sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
    {
        public ResetPasswordValidator()
        {
            RuleFor(x => x.UserId).GreaterThan(0).WithMessage("账号 Id 不合法");
            RuleFor(x => x.NewPassword).NotEmpty()
                .Must(p => !IsWeakPassword(p)).WithMessage(ValidationPatterns.passwordMessage);
        }
    }

    private sealed class ChangeUserStatusValidator : AbstractValidator<ChangeUserStatusCommand>
    {
        public ChangeUserStatusValidator()
        {
            RuleFor(x => x.UserId).GreaterThan(0).WithMessage("账号 Id 不合法");
            RuleFor(x => x.Status).Must(s => s is 1 or 2).WithMessage("状态只能是 1 启用 或 2 停用");
        }
    }

    private sealed class QueryUsersValidator : AbstractValidator<QueryUsersCommand>
    {
        public QueryUsersValidator()
        {
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须大于等于 1");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("每页条数需为 1-100");
        }
    }
}
