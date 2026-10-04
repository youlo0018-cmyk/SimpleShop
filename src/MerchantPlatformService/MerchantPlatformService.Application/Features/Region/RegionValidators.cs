using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantPlatformService.Application.Features.Region;

/// <summary>地区配置命令的校验器注册。</summary>
public static class RegionValidators
{
    /// <summary>注册全部地区校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddRegionValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<QueryRegionsCommand>, QueryRegionsValidator>();
        services.AddScoped<IValidator<SaveRegionsCommand>, SaveRegionsValidator>();
    }

    /// <summary>读取地区校验。</summary>
    private sealed class QueryRegionsValidator : AbstractValidator<QueryRegionsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryRegionsValidator()
            => RuleFor(x => x.PlatformId).GreaterThan(0).WithMessage("平台信息不正确");
    }

    /// <summary>保存地区校验。</summary>
    private sealed class SaveRegionsValidator : AbstractValidator<SaveRegionsCommand>
    {
        /// <summary>构造校验器。</summary>
        public SaveRegionsValidator()
        {
            RuleFor(x => x.PlatformId).GreaterThan(0).WithMessage("平台信息不正确");

            // 不在这里校验 JSON 内容：2MB 上限、合法 JSON、每级带 name
            // 这三条规则在 Handler 里用 MerchantRules 统一判断，
            // 在这里再写一遍就是两份口径，改一处漏一处
        }
    }
}
