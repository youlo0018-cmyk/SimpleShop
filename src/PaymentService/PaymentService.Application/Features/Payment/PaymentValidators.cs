using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace PaymentService.Application.Features.Payment;

/// <summary>支付命令的校验器注册。</summary>
/// <remarks>
/// 校验器写在嵌套静态类里，<c>AddValidatorsFromAssembly</c> 扫不到，必须逐条显式注册。
/// </remarks>
public static class PaymentValidators
{
    /// <summary>注册全部支付校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddPaymentValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<CreatePaymentCommand>, CreatePaymentValidator>();
        services.AddScoped<IValidator<ConfirmPaymentCommand>, ConfirmPaymentValidator>();
        services.AddScoped<IValidator<QueryPaymentCommand>, QueryPaymentValidator>();
        services.AddScoped<IValidator<SimulatePaymentCommand>, SimulatePaymentValidator>();
    }

    /// <summary>创建支付单校验。</summary>
    private sealed class CreatePaymentValidator : AbstractValidator<CreatePaymentCommand>
    {
        /// <summary>构造校验器。</summary>
        public CreatePaymentValidator()
            => RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("缺少订单号");
    }

    /// <summary>确认支付校验。</summary>
    private sealed class ConfirmPaymentValidator : AbstractValidator<ConfirmPaymentCommand>
    {
        /// <summary>构造校验器。</summary>
        public ConfirmPaymentValidator()
            => RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("缺少订单号");
    }

    /// <summary>查支付单校验。</summary>
    private sealed class QueryPaymentValidator : AbstractValidator<QueryPaymentCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryPaymentValidator()
            => RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("缺少订单号");
    }

    /// <summary>模拟支付校验。</summary>
    private sealed class SimulatePaymentValidator : AbstractValidator<SimulatePaymentCommand>
    {
        /// <summary>构造校验器。</summary>
        public SimulatePaymentValidator()
            => RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("缺少订单号");
    }
}
