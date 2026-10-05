using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace ToolService.Application.Features.Manage;

/// <summary>后台分页查文件。文件管理页的数据源。</summary>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
/// <param name="Keyword">按原始文件名模糊搜索。</param>
/// <param name="Category">分类过滤，空表示不限。</param>
public record QueryFilesCommand(
    int Page = 1,
    int PageSize = 20,
    string Keyword = "",
    string Category = "") : IRequest<ApiResponse<PagedResult<FileItem>>>;

/// <summary>删除文件。软删元数据，不删对象存储里的内容。</summary>
/// <param name="FileId">文件 Id。</param>
public record DeleteFileCommand(long FileId) : IRequest<ApiResponse>;

/// <summary>文件列表行。</summary>
/// <param name="FileId">文件 Id。</param>
/// <param name="OriginalName">原始文件名。</param>
/// <param name="Extension">扩展名，小写不含点。</param>
/// <param name="Category">分类值。</param>
/// <param name="CategoryName">分类中文名。</param>
/// <param name="Provider">存储后端。</param>
/// <param name="SizeBytes">文件大小，字节。</param>
/// <param name="SizeText">可读的文件大小。</param>
/// <param name="PublicUrl">对外访问地址。</param>
/// <param name="CreatedAt">上传时间。</param>
public sealed record FileItem(
    long FileId, string OriginalName, string Extension, string Category, string CategoryName,
    string Provider, long SizeBytes, string SizeText, string PublicUrl, string CreatedAt);

/// <summary>文件管理命令的校验器注册。</summary>
public static class ManageFileValidators
{
    /// <summary>允许的分类值。</summary>
    /// <remarks>
    /// 写死而不是从 <c>FileStorageOptions.MaxSizeBytes</c> 的键推导：
    /// 那个字典可能被运维在 AgileConfig 里加键，而这里是<b>分类白名单</b>，
    /// 两者不是一回事——运维加了个新分类但没在后台建筛选页，就会出现
    /// 「筛不出来」的分类选项。
    /// </remarks>
    private static readonly string[] Categories =
        ["image", "document", "audio", "video", "default"];

    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    /// <remarks>校验器写在嵌套静态类里，AddValidatorsFromAssembly 扫不到，必须显式注册。</remarks>
    public static void AddManageFileValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<QueryFilesCommand>, QueryFilesValidator>();
        services.AddScoped<IValidator<DeleteFileCommand>, DeleteFileValidator>();
    }

    /// <summary>文件分页查询校验。</summary>
    private sealed class QueryFilesValidator : AbstractValidator<QueryFilesCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryFilesValidator()
        {
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须为正数");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 200).WithMessage("每页条数不正确");
            RuleFor(x => x.Keyword).MaximumLength(255).WithMessage("搜索关键词过长");

            // 分类不校验就会静默查不到东西，用户以为「没有文件」，
            // 其实是自己筛错了分类值 —— 与页面校验器里筛券类型的处理同一思路。
            RuleFor(x => x.Category)
                .Must(c => string.IsNullOrWhiteSpace(c) || Categories.Contains(c.Trim(), StringComparer.OrdinalIgnoreCase))
                .WithMessage("文件分类不正确");
        }
    }

    /// <summary>删除文件校验。</summary>
    private sealed class DeleteFileValidator : AbstractValidator<DeleteFileCommand>
    {
        /// <summary>构造校验器。</summary>
        public DeleteFileValidator()
            => RuleFor(x => x.FileId).GreaterThan(0).WithMessage("文件信息不正确");
    }
}
