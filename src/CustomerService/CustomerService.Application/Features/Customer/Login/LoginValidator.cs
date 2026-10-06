using FluentValidation;

namespace CustomerService.Application.Features.Customer.Login;

/// <summary>客户登录校验器，覆盖 CustomerName 与 Password 的格式与长度。</summary>
/// <remarks>不校验密码是否正确——那需要查库与比对哈希，放 Handler。</remarks>
public class LoginValidator : AbstractValidator<LoginCommand>
{
    /// <summary>构造校验规则。</summary>
    public LoginValidator()
    {
        // WithMessage 只作用于紧挨着它的那一个校验器：写成 `NotEmpty().Length(3,64).WithMessage(...)`
        // 时，NotEmpty 失败会走 FluentValidation 的默认文案（带英文字段名），
        // 用户看到的就不是我们写的那句话。每条规则各配各的文案。
        RuleFor(x => x.CustomerName).NotEmpty().WithMessage("登录名必填");
        RuleFor(x => x.CustomerName).Length(3, 64).WithMessage("登录名必须为 3-64 个字符");

        RuleFor(x => x.Password).NotEmpty().WithMessage("密码必填");
        RuleFor(x => x.Password).MaximumLength(64).WithMessage("密码长度不能超过 64 个字符");
    }
}

