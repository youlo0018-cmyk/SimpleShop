using FluentValidation;

namespace CustomerService.Application.Features.Customer.Login;

/// <summary>客户登录校验器，覆盖 CustomerName 与 Password 的格式与长度。</summary>
/// <remarks>不校验密码是否正确——那需要查库与比对哈希，放 Handler。</remarks>
public class LoginValidator : AbstractValidator<LoginCommand>
{
    /// <summary>构造校验规则。</summary>
    public LoginValidator()
    {
        RuleFor(x => x.CustomerName).NotEmpty().Length(3, 64)
            .WithMessage("登录名必须为 3-64 个字符");

        RuleFor(x => x.Password).NotEmpty().MaximumLength(64)
            .WithMessage("密码长度不能超过 64 个字符");
    }
}

