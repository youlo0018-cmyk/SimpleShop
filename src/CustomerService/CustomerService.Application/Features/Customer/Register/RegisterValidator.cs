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
        // WithMessage 只作用于紧挨着它的那一个校验器：写成 `NotEmpty().Length(3,64).WithMessage(...)`
        // 时，NotEmpty 失败会走 FluentValidation 的默认文案（带英文字段名），
        // 用户看到的就不是我们写的那句话。每条规则各配各的文案。
        RuleFor(x => x.CustomerName).NotEmpty().WithMessage("登录名必填");
        RuleFor(x => x.CustomerName).Length(3, 64).WithMessage("登录名必须为 3-64 个字符");

        RuleFor(x => x.Password).NotEmpty().WithMessage("密码必填");
        RuleFor(x => x.Password).MinimumLength(ValidationPatterns.passwordMinLength)
            .WithMessage(ValidationPatterns.passwordMessage);
        // 判空必须写在谓词里：FluentValidation 默认级联是 Continue，前面 NotEmpty 失败后
        // 这条照样执行，`p.Any(...)` 在 null 上会抛 NullReferenceException ——
        // 接口回 500 而不是 400，而且错误信息是「服务器内部错误」。
        RuleFor(x => x.Password).Must(p => p is not null && p.Any(char.IsLetter) && p.Any(char.IsDigit))
            .WithMessage(ValidationPatterns.passwordMessage);

        RuleFor(x => x.Phone).NotEmpty().WithMessage("手机号必填");
        RuleFor(x => x.Phone).Matches(ValidationPatterns.phonePattern)
            .WithMessage(ValidationPatterns.phoneMessage);

        RuleFor(x => x.NickName).MaximumLength(64).WithMessage("昵称不能超过 64 个字符");
    }
}

