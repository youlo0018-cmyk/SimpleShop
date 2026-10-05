using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace PaymentService.Application.Features.Admin;

/// <summary>后台支付 / 退款查询的校验器注册。</summary>
public static class PaymentAdminValidators
{
    /// <summary>每页条数上限。</summary>
    /// <remarks>与其它后台列表一致，防止一次把整表拉出来。</remarks>
    private const int MaxPageSize = 200;

    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    /// <remarks>显式注册而不是只靠自动扫描：漏注册 = 该命令**完全没有校验**，
    /// 症状是「能提交出越界参数」，比启动报错难查得多。</remarks>
    public static void AddPaymentAdminValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<QueryAdminPaymentsCommand>, QueryAdminPaymentsValidator>();
        services.AddScoped<IValidator<QueryAdminRefundDetailCommand>, QueryAdminRefundDetailValidator>();
    }

    /// <summary>支付单分页校验。</summary>
    private sealed class QueryAdminPaymentsValidator : AbstractValidator<QueryAdminPaymentsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryAdminPaymentsValidator()
        {
            // 只认 0（不限）/ 1 / 20 / 30。不校验的话非法值会一路走到仓储，
            // 查不到就返回空列表 —— 用户以为「今天没有支付单」，其实是自己筛错了。
            RuleFor(x => x.Status).InclusiveBetween(0, 30).WithMessage("支付状态不正确");
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码不正确");
            RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize).WithMessage("每页条数不正确");
            RuleFor(x => x.Keyword).MaximumLength(64).WithMessage("搜索关键词过长");
            RuleFor(x => x.PlatformId).GreaterThanOrEqualTo(0).WithMessage("平台信息不正确");
            RuleFor(x => x.MerchantId).GreaterThanOrEqualTo(0).WithMessage("商户信息不正确");
        }
    }

    /// <summary>退款单详情校验。</summary>
    private sealed class QueryAdminRefundDetailValidator
        : AbstractValidator<QueryAdminRefundDetailCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryAdminRefundDetailValidator()
            => RuleFor(x => x.RefundId).GreaterThan(0).WithMessage("退款单信息不正确");
    }
}
