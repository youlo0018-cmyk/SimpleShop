using Collaboration.Domain.Common;
using MediatR;
using ProductService.Domain.Entities;
using ProductService.Domain.IRepository;

namespace ProductService.Application.Features.Logistics;

/// <summary>新建物流公司处理器。</summary>
public sealed class CreateLogisticsCompanyHandler
    : IRequestHandler<CreateLogisticsCompanyCommand, ApiResponse<long>>
{
    private readonly ILogisticsCompanyRepository _companies;

    /// <summary>构造处理器。</summary>
    /// <param name="companies">物流公司仓储。</param>
    public CreateLogisticsCompanyHandler(ILogisticsCompanyRepository companies) => _companies = companies;

    /// <inheritdoc />
    /// <remarks>
    /// 名称唯一靠数据库的 partial unique 索引兜底，这里只是**提前给出可读的报错**。
    /// 并发下两个请求可能都通过这一行检查，最终靠索引让其中一个失败——
    /// 所以索引是幂等的最后一道闸，这段校验只是体验优化，不是正确性依赖。
    /// </remarks>
    public async Task<ApiResponse<long>> Handle(
        CreateLogisticsCompanyCommand request, CancellationToken ct)
    {
        var name = request.CompanyName.Trim();

        if (await _companies.ExistsByNameAsync(name, 0, ct).ConfigureAwait(false))
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.Conflict, $"物流公司「{name}」已存在");
        }

        var company = new LogisticsCompany
        {
            CompanyName = name,
            CompanyCode = request.CompanyCode?.Trim() ?? string.Empty,
            Logo = request.Logo?.Trim() ?? string.Empty,
            SortOrder = request.SortOrder,
            Status = request.Status,
            PlatformId = request.PlatformId
        };

        var id = await _companies.InsertAsync(company, ct).ConfigureAwait(false);
        return ApiResults.Ok(id, "物流公司已创建");
    }
}

/// <summary>编辑物流公司处理器。</summary>
public sealed class UpdateLogisticsCompanyHandler
    : IRequestHandler<UpdateLogisticsCompanyCommand, ApiResponse>
{
    private readonly ILogisticsCompanyRepository _companies;

    /// <summary>构造处理器。</summary>
    /// <param name="companies">物流公司仓储。</param>
    public UpdateLogisticsCompanyHandler(ILogisticsCompanyRepository companies) => _companies = companies;

    /// <inheritdoc />
    public async Task<ApiResponse> Handle(
        UpdateLogisticsCompanyCommand request, CancellationToken ct)
    {
        var company = await _companies.GetByIdAsync(request.LogisticsId, ct).ConfigureAwait(false);
        if (company is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "物流公司不存在");

        var name = request.CompanyName.Trim();
        if (await _companies.ExistsByNameAsync(name, request.LogisticsId, ct).ConfigureAwait(false))
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.Conflict, $"物流公司「{name}」已存在");
        }

        company.CompanyName = name;
        company.CompanyCode = request.CompanyCode?.Trim() ?? string.Empty;
        company.Logo = request.Logo?.Trim() ?? string.Empty;
        company.SortOrder = request.SortOrder;
        company.Status = request.Status;
        company.Remark = request.Remark?.Trim() ?? string.Empty;

        await _companies.UpdateAsync(company, ct).ConfigureAwait(false);
        return ApiResponseFactory.Ok("物流公司已更新");
    }
}

/// <summary>删除物流公司处理器。</summary>
public sealed class DeleteLogisticsCompanyHandler
    : IRequestHandler<DeleteLogisticsCompanyCommand, ApiResponse>
{
    private readonly ILogisticsCompanyRepository _companies;

    /// <summary>构造处理器。</summary>
    /// <param name="companies">物流公司仓储。</param>
    public DeleteLogisticsCompanyHandler(ILogisticsCompanyRepository companies) => _companies = companies;

    /// <inheritdoc />
    /// <remarks>
    /// **不做引用校验**：发货单存的是公司名的**快照**，不是外键，
    /// 所以删掉字典项不会让任何历史发货单变成孤儿（显示的还是那个名字）。
    /// 若改成外键引用，这里才需要「有发货单就禁止删除」。
    /// </remarks>
    public async Task<ApiResponse> Handle(
        DeleteLogisticsCompanyCommand request, CancellationToken ct)
    {
        var company = await _companies.GetByIdAsync(request.LogisticsId, ct).ConfigureAwait(false);
        if (company is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "物流公司不存在");

        await _companies.DeleteAsync(request.LogisticsId, ct).ConfigureAwait(false);
        return ApiResponseFactory.Ok("物流公司已删除");
    }
}

/// <summary>分页查询物流公司处理器。</summary>
public sealed class QueryLogisticsCompaniesHandler
    : IRequestHandler<QueryLogisticsCompaniesCommand, ApiResponse<PagedResult<LogisticsCompanyItem>>>
{
    private readonly ILogisticsCompanyRepository _companies;

    /// <summary>构造处理器。</summary>
    /// <param name="companies">物流公司仓储。</param>
    public QueryLogisticsCompaniesHandler(ILogisticsCompanyRepository companies) => _companies = companies;

    /// <inheritdoc />
    public async Task<ApiResponse<PagedResult<LogisticsCompanyItem>>> Handle(
        QueryLogisticsCompaniesCommand request, CancellationToken ct)
    {
        var page = await _companies.QueryPagedAsync(
            request.Page, request.PageSize, request.Keyword, request.Status, ct).ConfigureAwait(false);

        var items = page.Items.Select(a => new LogisticsCompanyItem(
            a.Id, a.CompanyName, a.CompanyCode, a.Logo,
            a.SortOrder, a.Status, LogisticsCompanyStatuses.NameOf(a.Status), a.Remark,
            a.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss"))).ToList();

        return ApiResults.Ok(new PagedResult<LogisticsCompanyItem>(items, page.Total, request.Page, request.PageSize));
    }
}

/// <summary>物流公司下拉查询处理器。</summary>
public sealed class QueryLogisticsOptionsHandler
    : IRequestHandler<QueryLogisticsOptionsCommand, ApiResponse<List<LogisticsOptionItem>>>
{
    private readonly ILogisticsCompanyRepository _companies;

    /// <summary>构造处理器。</summary>
    /// <param name="companies">物流公司仓储。</param>
    public QueryLogisticsOptionsHandler(ILogisticsCompanyRepository companies) => _companies = companies;

    /// <inheritdoc />
    /// <remarks>
    /// 下拉只回 Id + 名称 + 编码（DATA_SPEC 4.1「下拉框显示 name，绝不显示 id」）：
    /// 编码留着是为了发货时按编码存一份，将来接快递鸟查单号不用改表。
    /// </remarks>
    public async Task<ApiResponse<List<LogisticsOptionItem>>> Handle(
        QueryLogisticsOptionsCommand request, CancellationToken ct)
    {
        var rows = await _companies.ListEnabledAsync(request.Keyword, ct).ConfigureAwait(false);
        var items = rows.Select(a => new LogisticsOptionItem(a.Id, a.CompanyName, a.CompanyCode)).ToList();
        return ApiResults.Ok(items);
    }
}
