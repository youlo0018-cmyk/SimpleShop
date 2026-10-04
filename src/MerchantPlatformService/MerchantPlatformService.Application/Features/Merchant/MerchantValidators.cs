using FluentValidation;
using MerchantPlatformService.Domain.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantPlatformService.Application.Features.Merchant;

/// <summary>商户命令的校验器注册。</summary>
/// <remarks>
/// 校验器写在嵌套静态类里，<c>AddValidatorsFromAssembly</c> 扫不到，必须逐条显式注册。
/// 只写不注册 = 完全没有校验。
/// </remarks>
public static class MerchantValidators
{
    /// <summary>注册全部商户校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddMerchantValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<CreateMerchantCommand>, CreateMerchantValidator>();
        services.AddScoped<IValidator<UpdateMerchantCommand>, UpdateMerchantValidator>();
        services.AddScoped<IValidator<AuditMerchantCommand>, AuditMerchantValidator>();
        services.AddScoped<IValidator<DeleteMerchantCommand>, DeleteMerchantValidator>();
        services.AddScoped<IValidator<ResubmitMerchantCommand>, ResubmitMerchantValidator>();
        services.AddScoped<IValidator<QueryMerchantsCommand>, QueryMerchantsValidator>();
    }

    /// <summary>创建商户校验。</summary>
    private sealed class CreateMerchantValidator : AbstractValidator<CreateMerchantCommand>
    {
        /// <summary>构造校验器。</summary>
        public CreateMerchantValidator()
        {
            RuleFor(x => x.MerchantName).NotEmpty().Length(2, 64).WithMessage("商户名称必须为 2-64 个字符");
            RuleFor(x => x.PlatformId).GreaterThan(0).WithMessage("请选择所属平台");
            RuleFor(x => x.ContactName).NotEmpty().Length(2, 32).WithMessage("联系人必须为 2-32 个字符");
            RuleFor(x => x.ContactPhone).Must(v => MerchantRules.ValidatePhone(v) == null).WithMessage("请填写正确的手机号");
            RuleFor(x => x.Logo).MaximumLength(512).WithMessage("Logo 地址过长");
            RuleFor(x => x.Description).MaximumLength(1000).WithMessage("店铺简介最多 1000 个字符");
            RuleFor(x => x.Status).Must(s => s is 1 or 2).WithMessage("状态只能是 1 启用 或 2 停用");
            RuleFor(x => x.Remark).MaximumLength(512).WithMessage("备注最多 512 个字符");
        }
    }

    /// <summary>编辑商户校验。</summary>
    private sealed class UpdateMerchantValidator : AbstractValidator<UpdateMerchantCommand>
    {
        /// <summary>构造校验器。</summary>
        public UpdateMerchantValidator()
        {
            RuleFor(x => x.MerchantId).GreaterThan(0).WithMessage("商户信息不正确");
            RuleFor(x => x.MerchantName).NotEmpty().Length(2, 64).WithMessage("商户名称必须为 2-64 个字符");
            RuleFor(x => x.ContactName).NotEmpty().Length(2, 32).WithMessage("联系人必须为 2-32 个字符");
            RuleFor(x => x.ContactPhone).Must(v => MerchantRules.ValidatePhone(v) == null).WithMessage("请填写正确的手机号");
            RuleFor(x => x.Logo).MaximumLength(512).WithMessage("Logo 地址过长");
            RuleFor(x => x.Description).MaximumLength(1000).WithMessage("店铺简介最多 1000 个字符");
            RuleFor(x => x.Status).Must(s => s is 1 or 2).WithMessage("状态只能是 1 启用 或 2 停用");
            RuleFor(x => x.Remark).MaximumLength(512).WithMessage("备注最多 512 个字符");
        }
    }

    /// <summary>审核商户校验。</summary>
    private sealed class AuditMerchantValidator : AbstractValidator<AuditMerchantCommand>
    {
        /// <summary>构造校验器。</summary>
        public AuditMerchantValidator()
        {
            RuleFor(x => x.MerchantId).GreaterThan(0).WithMessage("商户信息不正确");
            // 只能填 20 已通过 / 90 已拒绝：把「待审核」当成结论提交会让状态机出现
            // 「审核过了但还是待审核」的矛盾记录
            RuleFor(x => x.AuditStatus).Must(Domain.Entities.MerchantAuditStatuses.IsConclusion)
                .WithMessage("审核结论只能是已通过或已拒绝");
            RuleFor(x => x.AuditorId).GreaterThan(0).WithMessage("请先登录");
            RuleFor(x => x.AuditRemark).MaximumLength(500).WithMessage("审核意见最多 500 个字符");

            // 拒绝时必须写原因：商户要知道为什么被拒，否则只能反复提交碰运气
            RuleFor(x => x.AuditRemark)
                .Must((cmd, remark) => cmd.AuditStatus != Domain.Entities.MerchantAuditStatuses.Rejected
                    || !string.IsNullOrWhiteSpace(remark))
                .WithMessage("拒绝时必须填写原因");
        }
    }

    /// <summary>删除商户校验。</summary>
    private sealed class DeleteMerchantValidator : AbstractValidator<DeleteMerchantCommand>
    {
        /// <summary>构造校验器。</summary>
        public DeleteMerchantValidator()
            => RuleFor(x => x.MerchantId).GreaterThan(0).WithMessage("商户信息不正确");
    }

    /// <summary>重新提交审核校验。</summary>
    private sealed class ResubmitMerchantValidator : AbstractValidator<ResubmitMerchantCommand>
    {
        /// <summary>构造校验器。</summary>
        public ResubmitMerchantValidator()
            => RuleFor(x => x.MerchantId).GreaterThan(0).WithMessage("商户信息不正确");
    }

    /// <summary>商户列表校验。</summary>
    private sealed class QueryMerchantsValidator : AbstractValidator<QueryMerchantsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryMerchantsValidator()
        {
            RuleFor(x => x.PlatformId).GreaterThanOrEqualTo(0).WithMessage("平台 Id 不正确");
            RuleFor(x => x.AuditStatus).InclusiveBetween(0, 90).WithMessage("审核状态不正确");
            RuleFor(x => x.Status).InclusiveBetween(0, 2).WithMessage("状态不正确");
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码不正确");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("每页条数不正确");
        }
    }
}
