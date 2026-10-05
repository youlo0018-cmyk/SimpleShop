using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using ProductService.Domain.Entities;

namespace ProductService.Application.Features.Logistics;

/// <summary>新建物流公司。</summary>
/// <param name="CompanyName">公司名称，如「顺丰速运」。</param>
/// <param name="PlatformId">归属平台 Id，0 表示全平台共用。</param>
/// <param name="CompanyCode">公司编码，如 <c>sf</c>。可空。</param>
/// <param name="Logo">Logo URL，走 ToolService 上传。可空。</param>
/// <param name="SortOrder">排序，小的在前。</param>
/// <param name="Status">状态。1 启用 / 2 停用。</param>
public record CreateLogisticsCompanyCommand(
    string CompanyName,
    long PlatformId,
    string CompanyCode = "",
    string Logo = "",
    int SortOrder = 0,
    int Status = LogisticsCompanyStatuses.Enabled) : IRequest<ApiResponse<long>>;

/// <summary>编辑物流公司。</summary>
/// <param name="LogisticsId">物流公司 Id。</param>
/// <param name="CompanyName">公司名称。</param>
/// <param name="CompanyCode">公司编码，可空。</param>
/// <param name="Logo">Logo URL，可空。</param>
/// <param name="SortOrder">排序，小的在前。</param>
/// <param name="Status">状态。1 启用 / 2 停用。</param>
/// <param name="Remark">备注，可空。</param>
public record UpdateLogisticsCompanyCommand(
    long LogisticsId,
    string CompanyName,
    string CompanyCode = "",
    string Logo = "",
    int SortOrder = 0,
    int Status = LogisticsCompanyStatuses.Enabled,
    string Remark = "") : IRequest<ApiResponse>;

/// <summary>删除物流公司（软删）。</summary>
/// <param name="LogisticsId">物流公司 Id。</param>
public record DeleteLogisticsCompanyCommand(long LogisticsId) : IRequest<ApiResponse>;

/// <summary>分页查询物流公司。</summary>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
/// <param name="Keyword">按公司名或编码模糊搜索。</param>
/// <param name="Status">状态过滤，0 表示不限。</param>
public record QueryLogisticsCompaniesCommand(
    int Page = 1,
    int PageSize = 20,
    string Keyword = "",
    int Status = 0) : IRequest<ApiResponse<PagedResult<LogisticsCompanyItem>>>;

/// <summary>取启用中的物流公司，供发货表单下拉使用。</summary>
/// <param name="Keyword">按公司名或编码模糊搜索，空表示不过滤。</param>
public record QueryLogisticsOptionsCommand(string Keyword = "")
    : IRequest<ApiResponse<List<LogisticsOptionItem>>>;

/// <summary>物流公司列表行。</summary>
/// <param name="LogisticsId">物流公司 Id。</param>
/// <param name="CompanyName">公司名称。</param>
/// <param name="CompanyCode">公司编码。</param>
/// <param name="Logo">Logo URL。</param>
/// <param name="SortOrder">排序值。</param>
/// <param name="Status">状态值。</param>
/// <param name="StatusName">状态中文名。</param>
/// <param name="Remark">备注。</param>
/// <param name="UpdatedAt">最后更新时间，未更新过为空。</param>
public sealed record LogisticsCompanyItem(
    long LogisticsId, string CompanyName, string CompanyCode, string Logo,
    int SortOrder, int Status, string StatusName, string Remark, string? UpdatedAt);

/// <summary>物流公司下拉项。</summary>
/// <param name="LogisticsId">物流公司 Id。</param>
/// <param name="CompanyName">公司名称。</param>
/// <param name="CompanyCode">公司编码。</param>
public sealed record LogisticsOptionItem(long LogisticsId, string CompanyName, string CompanyCode);

/// <summary>物流公司命令的校验器注册。</summary>
public static class LogisticsValidators
{
    /// <summary>每页条数上限。</summary>
    private const int MaxPageSize = 100;

    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    /// <remarks>校验器写在嵌套静态类里，AddValidatorsFromAssembly 扫不到，必须显式注册。</remarks>
    public static void AddLogisticsValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<CreateLogisticsCompanyCommand>, CreateLogisticsCompanyValidator>();
        services.AddScoped<IValidator<UpdateLogisticsCompanyCommand>, UpdateLogisticsCompanyValidator>();
        services.AddScoped<IValidator<DeleteLogisticsCompanyCommand>, DeleteLogisticsCompanyValidator>();
        services.AddScoped<IValidator<QueryLogisticsCompaniesCommand>, QueryLogisticsCompaniesValidator>();
        services.AddScoped<IValidator<QueryLogisticsOptionsCommand>, QueryLogisticsOptionsValidator>();
    }

    /// <summary>新建物流公司校验。</summary>
    private sealed class CreateLogisticsCompanyValidator : AbstractValidator<CreateLogisticsCompanyCommand>
    {
        /// <summary>构造校验器。</summary>
        public CreateLogisticsCompanyValidator()
        {
            RuleFor(x => x.CompanyName).NotEmpty().Length(1, 64).WithMessage("物流公司名必须为 1-64 个字符");
            RuleFor(x => x.CompanyCode).MaximumLength(32).WithMessage("物流公司编码过长");
            RuleFor(x => x.Logo).MaximumLength(512).WithMessage("物流公司 Logo 地址过长");
            RuleFor(x => x.PlatformId).GreaterThanOrEqualTo(0).WithMessage("归属平台不正确");
            RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0).WithMessage("排序不能为负数");
            RuleFor(x => x.Status).Must(s => s is LogisticsCompanyStatuses.Enabled or LogisticsCompanyStatuses.Disabled)
                .WithMessage("状态只能是 1 启用 或 2 停用");
        }
    }

    /// <summary>编辑物流公司校验。</summary>
    private sealed class UpdateLogisticsCompanyValidator : AbstractValidator<UpdateLogisticsCompanyCommand>
    {
        /// <summary>构造校验器。</summary>
        public UpdateLogisticsCompanyValidator()
        {
            RuleFor(x => x.LogisticsId).GreaterThan(0).WithMessage("物流公司 Id 必须为正数");
            RuleFor(x => x.CompanyName).NotEmpty().Length(1, 64).WithMessage("物流公司名必须为 1-64 个字符");
            RuleFor(x => x.CompanyCode).MaximumLength(32).WithMessage("物流公司编码过长");
            RuleFor(x => x.Logo).MaximumLength(512).WithMessage("物流公司 Logo 地址过长");
            RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0).WithMessage("排序不能为负数");
            RuleFor(x => x.Status).Must(s => s is LogisticsCompanyStatuses.Enabled or LogisticsCompanyStatuses.Disabled)
                .WithMessage("状态只能是 1 启用 或 2 停用");
            RuleFor(x => x.Remark).MaximumLength(512).WithMessage("备注过长");
        }
    }

    /// <summary>删除物流公司校验。</summary>
    private sealed class DeleteLogisticsCompanyValidator : AbstractValidator<DeleteLogisticsCompanyCommand>
    {
        /// <summary>构造校验器。</summary>
        public DeleteLogisticsCompanyValidator()
            => RuleFor(x => x.LogisticsId).GreaterThan(0).WithMessage("物流公司 Id 必须为正数");
    }

    /// <summary>分页查询校验。</summary>
    private sealed class QueryLogisticsCompaniesValidator : AbstractValidator<QueryLogisticsCompaniesCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryLogisticsCompaniesValidator()
        {
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须为正数");
            RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize).WithMessage("每页条数不正确");
            RuleFor(x => x.Keyword).MaximumLength(64).WithMessage("搜索关键词过长");
            RuleFor(x => x.Status).InclusiveBetween(0, 2).WithMessage("状态不正确");
        }
    }

    /// <summary>下拉查询校验。</summary>
    private sealed class QueryLogisticsOptionsValidator : AbstractValidator<QueryLogisticsOptionsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryLogisticsOptionsValidator()
            => RuleFor(x => x.Keyword).MaximumLength(64).WithMessage("搜索关键词过长");
    }
}
