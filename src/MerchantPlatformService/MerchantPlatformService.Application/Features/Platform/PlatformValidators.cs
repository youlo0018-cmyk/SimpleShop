using FluentValidation;
using MerchantPlatformService.Domain.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantPlatformService.Application.Features.Platform;

/// <summary>平台命令的校验器注册。</summary>
/// <remarks>
/// 校验器写成嵌套静态类里的私有类：这是本项目统一约定，
/// 代价是 <c>AddValidatorsFromAssembly</c> **扫不到**，必须在
/// <c>AddPlatformValidators</c> 里逐条注册。只写不注册 = 完全没有校验。
/// </remarks>
public static class PlatformValidators
{
    /// <summary>注册全部平台校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddPlatformValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<CreatePlatformCommand>, CreatePlatformValidator>();
        services.AddScoped<IValidator<UpdatePlatformCommand>, UpdatePlatformValidator>();
        services.AddScoped<IValidator<DeletePlatformCommand>, DeletePlatformValidator>();
        services.AddScoped<IValidator<QueryPlatformsCommand>, QueryPlatformsValidator>();
    }

    /// <summary>创建平台校验。</summary>
    private sealed class CreatePlatformValidator : AbstractValidator<CreatePlatformCommand>
    {
        /// <summary>构造校验器。</summary>
        public CreatePlatformValidator()
        {
            RuleFor(x => x.PlatformName).NotEmpty().Length(2, 64).WithMessage("平台名称必须为 2-64 个字符");
            RuleFor(x => x.PlatformCode).Must(v => MerchantRules.ValidatePlatformCode(v) == null).WithMessage("平台编码必须是 6 位字母");
            RuleFor(x => x.ContactName).NotEmpty().Length(2, 32).WithMessage("联系人必须为 2-32 个字符");
            RuleFor(x => x.ContactPhone).Must(v => MerchantRules.ValidatePhone(v) == null).WithMessage("请填写正确的手机号");
            RuleFor(x => x.MallName).NotEmpty().Length(2, 64).WithMessage("商城名称必须为 2-64 个字符");
            RuleFor(x => x.Logo).MaximumLength(512).WithMessage("Logo 地址过长");
            RuleFor(x => x.Notice).MaximumLength(500).WithMessage("公告最多 500 个字符");
            RuleFor(x => x.ShippingFee).GreaterThanOrEqualTo(0m).WithMessage("运费不能为负数");
            RuleFor(x => x.FreeShippingThreshold).GreaterThanOrEqualTo(0m).WithMessage("包邮门槛不能为负数");
            RuleFor(x => x.Status).Must(s => s is 1 or 2).WithMessage("状态只能是 1 启用 或 2 停用");
            RuleFor(x => x.Remark).MaximumLength(512).WithMessage("备注最多 512 个字符");
            RuleFor(x => x.PrimaryColor).Must(v => MerchantRules.ValidateColor(v) == null).WithMessage("主题色格式不正确");
            RuleFor(x => x.TabColor).Must(v => MerchantRules.ValidateColor(v) == null).WithMessage("TabBar 选中色格式不正确");
            RuleFor(x => x.BackgroundColor).Must(v => MerchantRules.ValidateColor(v) == null).WithMessage("背景色格式不正确");
        }
    }

    /// <summary>编辑平台校验。</summary>
    private sealed class UpdatePlatformValidator : AbstractValidator<UpdatePlatformCommand>
    {
        /// <summary>构造校验器。</summary>
        public UpdatePlatformValidator()
        {
            RuleFor(x => x.PlatformId).GreaterThan(0).WithMessage("平台信息不正确");
            RuleFor(x => x.PlatformName).NotEmpty().Length(2, 64).WithMessage("平台名称必须为 2-64 个字符");
            RuleFor(x => x.ContactName).NotEmpty().Length(2, 32).WithMessage("联系人必须为 2-32 个字符");
            RuleFor(x => x.ContactPhone).Must(v => MerchantRules.ValidatePhone(v) == null).WithMessage("请填写正确的手机号");
            RuleFor(x => x.MallName).NotEmpty().Length(2, 64).WithMessage("商城名称必须为 2-64 个字符");
            RuleFor(x => x.Logo).MaximumLength(512).WithMessage("Logo 地址过长");
            RuleFor(x => x.Notice).MaximumLength(500).WithMessage("公告最多 500 个字符");
            RuleFor(x => x.ShippingFee).GreaterThanOrEqualTo(0m).WithMessage("运费不能为负数");
            RuleFor(x => x.FreeShippingThreshold).GreaterThanOrEqualTo(0m).WithMessage("包邮门槛不能为负数");
            RuleFor(x => x.Status).Must(s => s is 1 or 2).WithMessage("状态只能是 1 启用 或 2 停用");
            RuleFor(x => x.Remark).MaximumLength(512).WithMessage("备注最多 512 个字符");
            RuleFor(x => x.PrimaryColor).Must(v => MerchantRules.ValidateColor(v) == null).WithMessage("主题色格式不正确");
            RuleFor(x => x.TabColor).Must(v => MerchantRules.ValidateColor(v) == null).WithMessage("TabBar 选中色格式不正确");
            RuleFor(x => x.BackgroundColor).Must(v => MerchantRules.ValidateColor(v) == null).WithMessage("背景色格式不正确");
        }
    }

    /// <summary>删除平台校验。</summary>
    private sealed class DeletePlatformValidator : AbstractValidator<DeletePlatformCommand>
    {
        /// <summary>构造校验器。</summary>
        public DeletePlatformValidator()
            => RuleFor(x => x.PlatformId).GreaterThan(0).WithMessage("平台信息不正确");
    }

    /// <summary>平台列表校验。</summary>
    private sealed class QueryPlatformsValidator : AbstractValidator<QueryPlatformsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryPlatformsValidator()
        {
            RuleFor(x => x.Status).InclusiveBetween(0, 2).WithMessage("状态不正确");
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码不正确");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("每页条数不正确");
        }
    }
}
