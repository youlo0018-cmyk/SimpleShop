using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;

namespace CustomerService.Application.Features.Customer.Profile;

/// <summary>客户资料视图（C 端「我的」页）。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="CustomerNo">客户编码，客服与订单检索用。</param>
/// <param name="CustomerName">登录名。</param>
/// <param name="Phone">手机号（打码后下发，完整号码只在下单时由服务端取）。</param>
/// <param name="NickName">昵称。</param>
/// <param name="Avatar">头像 URL。</param>
/// <param name="Gender">性别，见 <see cref="CustomerGenders"/>。</param>
/// <param name="GenderName">性别中文名，前端不猜枚举。</param>
/// <param name="Birthday">生日（yyyy-MM-dd），未填为空串。</param>
public sealed record CustomerProfileDto(
    string CustomerId, string CustomerNo, string CustomerName, string Phone,
    string NickName, string Avatar, int Gender, string GenderName, string Birthday);

/// <summary>查自己的资料。</summary>
/// <param name="CustomerId">客户 Id；经网关时必须与令牌一致。</param>
public record QueryCustomerProfileCommand(long CustomerId)
    : IRequest<ApiResponse<CustomerProfileDto>>;

/// <summary>改自己的资料。</summary>
/// <param name="CustomerId">客户 Id；经网关时必须与令牌一致。</param>
/// <param name="NickName">昵称，1-64 字符。</param>
/// <param name="Avatar">头像 URL，可空（不清空时前端回传原值）。</param>
/// <param name="Gender">性别，0 未知 / 1 男 / 2 女。</param>
/// <param name="Birthday">生日，可空。</param>
/// <remarks>
/// <b>登录名与手机号不在这里改</b>：两者都是唯一键，改手机号要先做短信验证，
/// 改登录名会让客服与订单检索的凭据失效。规格里「资料」指的是展示信息。
/// </remarks>
public record UpdateCustomerProfileCommand(
    long CustomerId, string NickName, string Avatar = "", int Gender = 0, DateTime? Birthday = null)
    : IRequest<ApiResponse<CustomerProfileDto>>;

/// <summary>性别。</summary>
public static class CustomerGenders
{
    /// <summary>未填。</summary>
    public const int Unknown = 0;

    /// <summary>男。</summary>
    public const int Male = 1;

    /// <summary>女。</summary>
    public const int Female = 2;

    /// <summary>性别中文名。</summary>
    /// <param name="gender">性别值。</param>
    /// <returns>中文名。</returns>
    public static string NameOf(int gender) => gender switch
    {
        Male => "男",
        Female => "女",
        _ => "未填写"
    };
}

/// <summary>查资料校验。</summary>
public sealed class QueryCustomerProfileValidator : AbstractValidator<QueryCustomerProfileCommand>
{
    /// <summary>构造校验器。</summary>
    public QueryCustomerProfileValidator()
        => RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户信息不正确");
}

/// <summary>改资料校验。</summary>
public sealed class UpdateCustomerProfileValidator : AbstractValidator<UpdateCustomerProfileCommand>
{
    /// <summary>构造校验器。</summary>
    public UpdateCustomerProfileValidator()
    {
        RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户信息不正确");
        // WithMessage 只作用于**紧挨着它的那一个**校验器：写成
        // `NotEmpty().MaximumLength(64).WithMessage(...)` 时，NotEmpty 失败会走
        // FluentValidation 的默认文案（「'Nick Name' 不能为空。」），
        // 前端输入框下方就会出现一句带英文字段名的提示。两条规则各写各的文案。
        RuleFor(x => x.NickName).NotEmpty().WithMessage("昵称必填");
        RuleFor(x => x.NickName).MaximumLength(64).WithMessage("昵称不超过 64 个字符");
        RuleFor(x => x.Avatar).MaximumLength(512).WithMessage("头像地址过长");
        RuleFor(x => x.Gender).Must(g => g is CustomerGenders.Unknown or CustomerGenders.Male or CustomerGenders.Female)
            .WithMessage("性别不正确");
        RuleFor(x => x.Birthday)
            .Must(b => b is null || b.Value.Year >= 1900)
            .WithMessage("生日不正确");
        RuleFor(x => x.Birthday)
            .Must(b => b is null || b.Value.Date <= DateTime.UtcNow.Date)
            .WithMessage("生日不能是未来日期");
    }
}
