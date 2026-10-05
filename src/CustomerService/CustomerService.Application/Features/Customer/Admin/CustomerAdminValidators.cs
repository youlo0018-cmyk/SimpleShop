using CustomerService.Domain.Entities;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CustomerService.Application.Features.Customer.Admin;

/// <summary>后台客户命令的校验器注册。</summary>
public static class CustomerAdminValidators
{
    /// <summary>每页条数上限。</summary>
    /// <remarks>与后台其他列表保持一致，防止一次把整表拉出来。</remarks>
    private const int MaxPageSize = 200;

    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    /// <remarks>
    /// 必须显式注册：写在这里的校验器是 public 类，`AddValidatorsFromAssembly` 能扫到，
    /// 但**本项目约定是不依赖自动扫描**——扫不到时是「完全没有校验」，
    /// 症状是「用户能提交出脏数据」，比启动报错难查得多。
    /// </remarks>
    public static void AddCustomerAdminValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<QueryAdminCustomersCommand>, QueryAdminCustomersValidator>();
        services.AddScoped<IValidator<QueryAdminCustomerDetailCommand>, CustomerIdValidator>();
        services.AddScoped<IValidator<ChangeCustomerStatusCommand>, ChangeCustomerStatusValidator>();
    }

    /// <summary>后台客户列表校验。</summary>
    private sealed class QueryAdminCustomersValidator : AbstractValidator<QueryAdminCustomersCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryAdminCustomersValidator()
        {
            // 状态只认 0（不限）或 1/2。不校验的话非法值会走到仓储，
            // 查不到数据就返回空列表 —— 用户以为「没有这个客户」，其实是他筛选条件写错了。
            RuleFor(x => x.Status).InclusiveBetween(0, 2).WithMessage("客户状态不正确");
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码不正确");
            RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize).WithMessage("每页条数不正确");
            RuleFor(x => x.Keyword).MaximumLength(64).WithMessage("搜索关键词过长");
        }
    }

    /// <summary>客户 Id 校验（详情用）。</summary>
    private sealed class CustomerIdValidator : AbstractValidator<QueryAdminCustomerDetailCommand>
    {
        /// <summary>构造校验器。</summary>
        public CustomerIdValidator()
            => RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("客户信息不正确");
    }

    /// <summary>启用 / 停用校验。</summary>
    private sealed class ChangeCustomerStatusValidator : AbstractValidator<ChangeCustomerStatusCommand>
    {
        /// <summary>构造校验器。</summary>
        public ChangeCustomerStatusValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("客户信息不正确");
            // 只认 1 正常 / 2 停用。这里放过别的值会写进库里一个前端不认识的状态，
            // 之后所有「是否停用」的判断都会把它当成正常客户。
            RuleFor(x => x.Status)
                .Must(a => a == CustomerStatuses.Enabled || a == CustomerStatuses.Disabled)
                .WithMessage("客户状态不正确");
        }
    }
}
