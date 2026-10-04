using FluentValidation;
using MerchantPlatformService.Domain.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantPlatformService.Application.Features.Design;

/// <summary>装修命令的校验器注册。</summary>
/// <remarks>
/// 校验器写在嵌套静态类里，<c>AddValidatorsFromAssembly</c> 扫不到，必须逐条显式注册。
/// </remarks>
public static class DesignValidators
{
    /// <summary>配置 JSON 的体积上限。</summary>
    /// <remarks>
    /// 一个首页塞满组件也就几百 KB。给 2MB 上限是为了挡住「把整个商品库塞进 props」
    /// 这种误操作——它不会报错，只会让每次读取装修都拖着一大坨无用数据。
    /// </remarks>
    public const int MaxConfigBytes = 2 * 1024 * 1024;

    /// <summary>注册全部装修校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddDesignValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<QueryPlatformDesignCommand>, QueryPlatformDesignValidator>();
        services.AddScoped<IValidator<QueryMerchantDesignCommand>, QueryMerchantDesignValidator>();
        services.AddScoped<IValidator<SavePlatformDraftCommand>, SavePlatformDraftValidator>();
        services.AddScoped<IValidator<SaveMerchantDraftCommand>, SaveMerchantDraftValidator>();
        services.AddScoped<IValidator<PublishPlatformDesignCommand>, PublishPlatformDesignValidator>();
        services.AddScoped<IValidator<PublishMerchantDesignCommand>, PublishMerchantDesignValidator>();
    }

    /// <summary>配置体积校验（平台与商户草稿共用）。</summary>
    /// <typeparam name="T">命令类型。</typeparam>
    private abstract class DraftValidator<T> : AbstractValidator<T> where T : class
    {
        /// <summary>构造校验器。</summary>
        /// <param name="idSelector">目标 Id 字段。</param>
        /// <param name="jsonSelector">配置 JSON 字段。</param>
        protected DraftValidator(
            System.Linq.Expressions.Expression<Func<T, long>> idSelector,
            System.Linq.Expressions.Expression<Func<T, string>> jsonSelector)
        {
            RuleFor(idSelector).GreaterThan(0).WithMessage("目标信息不正确");
            RuleFor(jsonSelector).NotEmpty().WithMessage("请填写装修配置");
            RuleFor(jsonSelector).Must(IsSizeOk).WithMessage($"装修配置过大（上限 {MaxConfigBytes / 1024 / 1024}MB）");
        }

        /// <summary>配置 JSON 是否在体积上限内。</summary>
        /// <param name="json">配置 JSON。</param>
        /// <returns>在上限内返回 true。</returns>
        private static bool IsSizeOk(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            return System.Text.Encoding.UTF8.GetByteCount(json) <= MaxConfigBytes;
        }
    }

    /// <summary>读平台装修校验。</summary>
    private sealed class QueryPlatformDesignValidator : AbstractValidator<QueryPlatformDesignCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryPlatformDesignValidator()
            => RuleFor(x => x.PlatformId).GreaterThan(0).WithMessage("平台信息不正确");
    }

    /// <summary>读商户装修校验。</summary>
    private sealed class QueryMerchantDesignValidator : AbstractValidator<QueryMerchantDesignCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryMerchantDesignValidator()
            => RuleFor(x => x.MerchantId).GreaterThan(0).WithMessage("商户信息不正确");
    }

    /// <summary>保存平台草稿校验。</summary>
    private sealed class SavePlatformDraftValidator : DraftValidator<SavePlatformDraftCommand>
    {
        /// <summary>构造校验器。</summary>
        public SavePlatformDraftValidator() : base(x => x.PlatformId, x => x.ConfigJson) { }
    }

    /// <summary>保存商户草稿校验。</summary>
    private sealed class SaveMerchantDraftValidator : DraftValidator<SaveMerchantDraftCommand>
    {
        /// <summary>构造校验器。</summary>
        public SaveMerchantDraftValidator() : base(x => x.MerchantId, x => x.ConfigJson) { }
    }

    /// <summary>发布平台装修校验。</summary>
    private sealed class PublishPlatformDesignValidator : AbstractValidator<PublishPlatformDesignCommand>
    {
        /// <summary>构造校验器。</summary>
        public PublishPlatformDesignValidator()
            => RuleFor(x => x.PlatformId).GreaterThan(0).WithMessage("平台信息不正确");
    }

    /// <summary>发布商户装修校验。</summary>
    private sealed class PublishMerchantDesignValidator : AbstractValidator<PublishMerchantDesignCommand>
    {
        /// <summary>构造校验器。</summary>
        public PublishMerchantDesignValidator()
            => RuleFor(x => x.MerchantId).GreaterThan(0).WithMessage("商户信息不正确");
    }
}
