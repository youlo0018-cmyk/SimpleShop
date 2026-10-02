using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace ProductService.Application.Features.Brand;

/// <summary>新建品牌。</summary>
public record CreateBrandCommand(
    string BrandName,
    long PlatformId,
    string Logo = "",
    int SortOrder = 0,
    int Status = 1) : IRequest<ApiResponse<long>>;

/// <summary>编辑品牌。</summary>
public record UpdateBrandCommand(
    long BrandId,
    string BrandName,
    string Logo = "",
    int SortOrder = 0,
    int Status = 1) : IRequest<ApiResponse>;

/// <summary>删除品牌。有商品时拒绝。</summary>
public record DeleteBrandCommand(long BrandId) : IRequest<ApiResponse>;

/// <summary>分页查询品牌。</summary>
public record QueryBrandsCommand(
    int Page = 1,
    int PageSize = 20,
    string Keyword = "",
    bool IncludeDisabled = false) : IRequest<ApiResponse<List<BrandListItem>>>;

/// <summary>品牌列表项。</summary>
public record BrandListItem(string Id, string BrandName, string Logo, int SortOrder, int Status);

/// <summary>品牌命令的校验器注册。</summary>
public static class BrandValidators
{
    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddBrandValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<CreateBrandCommand>, CreateBrandValidator>();
        services.AddScoped<IValidator<UpdateBrandCommand>, UpdateBrandValidator>();
        services.AddScoped<IValidator<DeleteBrandCommand>, DeleteBrandValidator>();
    }

    /// <summary>新建品牌校验。</summary>
    private sealed class CreateBrandValidator : AbstractValidator<CreateBrandCommand>
    {
        /// <summary>构造校验器。</summary>
        public CreateBrandValidator()
        {
            RuleFor(x => x.BrandName).NotEmpty().Length(1, 64).WithMessage("品牌名必须为 1-64 个字符");
            RuleFor(x => x.Logo).MaximumLength(512).WithMessage("品牌 Logo 地址过长");
            RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0).WithMessage("排序不能为负数");
            RuleFor(x => x.Status).Must(s => s is 1 or 2).WithMessage("状态只能是 1 启用 或 2 停用");
        }
    }

    /// <summary>编辑品牌校验。</summary>
    private sealed class UpdateBrandValidator : AbstractValidator<UpdateBrandCommand>
    {
        /// <summary>构造校验器。</summary>
        public UpdateBrandValidator()
        {
            RuleFor(x => x.BrandId).GreaterThan(0).WithMessage("品牌 Id 必须为正数");
            RuleFor(x => x.BrandName).NotEmpty().Length(1, 64).WithMessage("品牌名必须为 1-64 个字符");
            RuleFor(x => x.Logo).MaximumLength(512).WithMessage("品牌 Logo 地址过长");
            RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0).WithMessage("排序不能为负数");
            RuleFor(x => x.Status).Must(s => s is 1 or 2).WithMessage("状态只能是 1 启用 或 2 停用");
        }
    }

    /// <summary>删除品牌校验。</summary>
    private sealed class DeleteBrandValidator : AbstractValidator<DeleteBrandCommand>
    {
        /// <summary>构造校验器。</summary>
        public DeleteBrandValidator()
            => RuleFor(x => x.BrandId).GreaterThan(0).WithMessage("品牌 Id 必须为正数");
    }
}