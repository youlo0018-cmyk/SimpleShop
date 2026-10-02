using Collaboration.Domain.Common;
using Collaboration.Domain.Validation;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace ProductService.Application.Features.Category;

/// <summary>分类层级上限。超过就是非法（DATA_SPEC 5.4）。</summary>
public static class CategoryLevels
{
    /// <summary>最深层级。</summary>
    public const int Max = 3;
}

/// <summary>新建分类。</summary>
public record CreateCategoryCommand(
    long ParentId,
    string CategoryName,
    long PlatformId,
    string CategoryCode = "",
    string Icon = "",
    string Image = "",
    int SortOrder = 0,
    int Status = 1) : IRequest<ApiResponse<long>>;

/// <summary>编辑分类。<b>不允许改 ParentId</b>——换父级等于换层级，会连带影响整棵子树。</summary>
public record UpdateCategoryCommand(
    long CategoryId,
    string CategoryName,
    string CategoryCode = "",
    string Icon = "",
    string Image = "",
    int SortOrder = 0,
    int Status = 1) : IRequest<ApiResponse>;

/// <summary>删除分类。有子分类或有商品时拒绝。</summary>
public record DeleteCategoryCommand(long CategoryId) : IRequest<ApiResponse>;

/// <summary>查询分类树，最多三级。</summary>
public record QueryCategoryTreeCommand(bool IncludeDisabled = false) : IRequest<ApiResponse<List<CategoryNodeDto>>>;

/// <summary>分类树节点。</summary>
/// <remarks>
/// 刻意用 class 而不是 record：建树时要先造出所有节点、再把子节点挂上去
/// （HasChildren 也要从 false 改成 true），positional record 的属性是只读的，改不了。
/// </remarks>
public sealed class CategoryNodeDto
{
    /// <summary>分类 Id（字符串，雪花 Id 前端按字符串处理）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>上级分类 Id，0 表示一级。</summary>
    public long ParentId { get; set; }

    /// <summary>分类名称。</summary>
    public string CategoryName { get; set; } = string.Empty;

    /// <summary>分类编码。</summary>
    public string CategoryCode { get; set; } = string.Empty;

    /// <summary>分类图标 URL。</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>分类大图 URL。</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>排序，小的在前。</summary>
    public int SortOrder { get; set; }

    /// <summary>层级 1 / 2 / 3。</summary>
    public int Level { get; set; }

    /// <summary>状态。1 启用 / 2 停用。</summary>
    public int Status { get; set; }

    /// <summary>是否有子分类。前端据此决定要不要展开箭头。</summary>
    public bool HasChildren { get; set; }

    /// <summary>子分类列表。</summary>
    public List<CategoryNodeDto> Children { get; set; } = [];
}

/// <summary>分类命令的校验器注册。</summary>
public static class CategoryValidators
{
    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddCategoryValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<CreateCategoryCommand>, CreateCategoryValidator>();
        services.AddScoped<IValidator<UpdateCategoryCommand>, UpdateCategoryValidator>();
        services.AddScoped<IValidator<DeleteCategoryCommand>, DeleteCategoryValidator>();
    }

    /// <summary>新建分类校验。</summary>
    private sealed class CreateCategoryValidator : AbstractValidator<CreateCategoryCommand>
    {
        /// <summary>构造校验器。</summary>
        public CreateCategoryValidator()
        {
            RuleFor(x => x.CategoryName).NotEmpty().Length(1, 64).WithMessage("分类名称必须为 1-64 个字符");
            RuleFor(x => x.CategoryCode).MaximumLength(64).WithMessage("分类编码最多 64 个字符");
            RuleFor(x => x.Icon).MaximumLength(512).WithMessage("分类图标地址过长");
            RuleFor(x => x.Image).MaximumLength(512).WithMessage("分类大图地址过长");
            RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0).WithMessage("排序不能为负数");
            RuleFor(x => x.Status).Must(s => s is 1 or 2).WithMessage("状态只能是 1 启用 或 2 停用");
            RuleFor(x => x.ParentId).GreaterThanOrEqualTo(0).WithMessage("上级分类 Id 不能为负数");
        }
    }

    /// <summary>编辑分类校验。</summary>
    private sealed class UpdateCategoryValidator : AbstractValidator<UpdateCategoryCommand>
    {
        /// <summary>构造校验器。</summary>
        public UpdateCategoryValidator()
        {
            RuleFor(x => x.CategoryId).GreaterThan(0).WithMessage("分类 Id 必须为正数");
            RuleFor(x => x.CategoryName).NotEmpty().Length(1, 64).WithMessage("分类名称必须为 1-64 个字符");
            RuleFor(x => x.CategoryCode).MaximumLength(64).WithMessage("分类编码最多 64 个字符");
            RuleFor(x => x.Icon).MaximumLength(512).WithMessage("分类图标地址过长");
            RuleFor(x => x.Image).MaximumLength(512).WithMessage("分类大图地址过长");
            RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0).WithMessage("排序不能为负数");
            RuleFor(x => x.Status).Must(s => s is 1 or 2).WithMessage("状态只能是 1 启用 或 2 停用");
        }
    }

    /// <summary>删除分类校验。</summary>
    private sealed class DeleteCategoryValidator : AbstractValidator<DeleteCategoryCommand>
    {
        /// <summary>构造校验器。</summary>
        public DeleteCategoryValidator()
            => RuleFor(x => x.CategoryId).GreaterThan(0).WithMessage("分类 Id 必须为正数");
    }
}