using Collaboration.Domain.Validation;
using FluentValidation;

namespace CustomerService.Application.Features.Customer.Register;

/// <summary>客户注册校验器。</summary>
/// <remarks>
/// 覆盖 CustomerName / Password / Phone / NickName 四个字段。
/// 正则与长度直接引用 ValidationPatterns（由 deploy/shared/validation-rules.json 生成，CODING_STANDARD 3.5），
/// 不在这里手写正则，避免前后端漂移。
/// 唯一性不在这里校验——那需要查库，放 Handler（CODING_STANDARD 3.3）。
/// </remarks>
public class RegisterValidator : AbstractValidator<RegisterCommand>
{
    /// <summary>构造校验规则。</summary>
    public RegisterValidator()
    {
        RuleFor(x => x.CustomerName).NotEmpty().Length(3, 64)
            .WithMessage("登录名必须为 3-64 个字符");

        RuleFor(x => x.Password).NotEmpty().MinimumLength(ValidationPatterns.passwordMinLength)
            .Must(p => p.Any(char.IsLetter) && p.Any(char.IsDigit))
            .WithMessage(ValidationPatterns.passwordMessage);

        RuleFor(x => x.Phone).NotEmpty().Matches(ValidationPatterns.phonePattern)
            .WithMessage(ValidationPatterns.phoneMessage);

        RuleFor(x => x.NickName).MaximumLength(64)
            .WithMessage("昵称不能超过 64 个字符");
    }
}

