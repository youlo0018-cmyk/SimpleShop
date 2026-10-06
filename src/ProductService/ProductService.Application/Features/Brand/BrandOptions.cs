using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using ProductService.Domain.IRepository;

namespace ProductService.Application.Features.Brand;

/// <summary>品牌下拉（DATA_SPEC 4.2）：只返回启用品牌，供商品表单选择。</summary>
/// <param name="Keyword">按品牌名模糊搜索，可空。</param>
/// <param name="Limit">最多返回多少条，1-200。</param>
public record QueryBrandOptionsCommand(string Keyword = "", int Limit = 200)
    : IRequest<ApiResponse<List<BrandOption>>>;

/// <summary>品牌下拉项。</summary>
/// <param name="Value">品牌 Id，<b>字符串下发</b>（4.6：雪花 Id 前端必须保持字符串）。</param>
/// <param name="Label">品牌名，前端直接显示。</param>
public sealed record BrandOption(string Value, string Label);

/// <summary>品牌下拉校验器注册。</summary>
public static class BrandOptionValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddBrandOptionValidators(IServiceCollection services)
        => services.AddScoped<IValidator<QueryBrandOptionsCommand>, QueryBrandOptionsValidator>();

    /// <summary>下拉查询校验。</summary>
    private sealed class QueryBrandOptionsValidator : AbstractValidator<QueryBrandOptionsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryBrandOptionsValidator()
        {
            RuleFor(x => x.Keyword).MaximumLength(64).WithMessage("关键词最多 64 个字符");
            RuleFor(x => x.Limit).InclusiveBetween(1, 200).WithMessage("下拉条数需为 1-200");
        }
    }
}

/// <summary>品牌下拉处理器。</summary>
public sealed class QueryBrandOptionsHandler
    : IRequestHandler<QueryBrandOptionsCommand, ApiResponse<List<BrandOption>>>
{
    private readonly IBrandRepository _brands;

    /// <summary>构造处理器。</summary>
    /// <param name="brands">品牌仓储。</param>
    public QueryBrandOptionsHandler(IBrandRepository brands) => _brands = brands;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>启用品牌的下拉项。</returns>
    /// <remarks>
    /// 复用分页查询并固定 <c>includeDisabled = false</c>：
    /// 下拉里出现停用品牌，运营选了之后保存会被后端拒（或存下一个已经停用的品牌），
    /// 属于「界面上能选、实际不能用」的典型坑。
    /// </remarks>
    public async Task<ApiResponse<List<BrandOption>>> Handle(
        QueryBrandOptionsCommand request, CancellationToken ct)
    {
        var (rows, _) = await _brands
            .QueryPagedAsync(1, request.Limit, request.Keyword ?? string.Empty, includeDisabled: false, ct)
            .ConfigureAwait(false);

        return ApiResults.Ok(rows.Select(a => new BrandOption(a.Id.ToString(), a.BrandName)).ToList());
    }
}
