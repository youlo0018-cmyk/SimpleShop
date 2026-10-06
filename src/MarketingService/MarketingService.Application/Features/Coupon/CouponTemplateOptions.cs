using Collaboration.Domain.Common;
using FluentValidation;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace MarketingService.Application.Features.Coupon;

/// <summary>券模板下拉（DATA_SPEC 4.2）：只返回**启用**模板，供券活动与满赠活动选择。</summary>
/// <param name="Keyword">按模板名模糊搜索，可空。</param>
/// <param name="Limit">最多返回多少条，1-200。</param>
public record QueryCouponTemplateOptionsCommand(string Keyword = "", int Limit = 200)
    : IRequest<ApiResponse<List<CouponTemplateOption>>>;

/// <summary>券模板下拉项。</summary>
/// <param name="Value">模板 Id，字符串下发。</param>
/// <param name="Label">模板名。</param>
/// <param name="CouponType">券类型，前端按它决定是否显示门槛字段。</param>
/// <param name="CouponTypeName">券类型中文名，**后端下发**（4.5）。</param>
public sealed record CouponTemplateOption(
    string Value, string Label, int CouponType, string CouponTypeName);

/// <summary>券模板下拉的校验器注册。</summary>
public static class CouponTemplateOptionValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddCouponTemplateOptionValidators(IServiceCollection services)
        => services.AddScoped<IValidator<QueryCouponTemplateOptionsCommand>, QueryCouponTemplateOptionsValidator>();

    /// <summary>下拉查询校验。</summary>
    private sealed class QueryCouponTemplateOptionsValidator
        : AbstractValidator<QueryCouponTemplateOptionsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryCouponTemplateOptionsValidator()
        {
            RuleFor(x => x.Keyword).MaximumLength(64).WithMessage("关键词最多 64 个字符");
            RuleFor(x => x.Limit).InclusiveBetween(1, 200).WithMessage("下拉条数需为 1-200");
        }
    }
}

/// <summary>券模板下拉处理器。</summary>
public sealed class QueryCouponTemplateOptionsHandler
    : IRequestHandler<QueryCouponTemplateOptionsCommand, ApiResponse<List<CouponTemplateOption>>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public QueryCouponTemplateOptionsHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>启用模板的下拉项。</returns>
    /// <remarks>
    /// 固定 <c>status = 1</c>：停用模板选进活动后，用户点「领取」只会拿到
    /// 「券模板不存在或已停用」—— 那是配置期就该挡住的错。
    /// 平台范围由 AOP 租户过滤注入（4.2 通用约定），这里不传 platformId。
    /// </remarks>
    public async Task<ApiResponse<List<CouponTemplateOption>>> Handle(
        QueryCouponTemplateOptionsCommand request, CancellationToken ct)
    {
        var page = await _coupons.PageTemplatesAsync(
            1, request.Limit, request.Keyword ?? string.Empty,
            couponType: 0, status: 1, platformId: 0, ct).ConfigureAwait(false);

        var list = page.Items.Select(a => new CouponTemplateOption(
            a.Id.ToString(), a.TemplateName, a.CouponType, CouponTypes.NameOf(a.CouponType))).ToList();

        return ApiResults.Ok(list);
    }
}
