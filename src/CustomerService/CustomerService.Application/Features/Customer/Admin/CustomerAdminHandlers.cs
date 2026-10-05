using Collaboration.Domain.Common;
using CustomerService.Domain.Entities;
using CustomerService.Domain.IRepository;
using MediatR;

// 🔴 必须用别名（CODING_STANDARD 陷阱 1）：本文件位于
// `Features.Customer.Admin` 命名空间里，而它正好叫 `Customer` ——
// 于是 `Customer` 在这里解析成**命名空间**而不是实体，CS0118 直接编译不过。
// 报错只说「命名空间被当成类型」，不指明是哪个名字遮蔽了哪个，要靠这条规则才想得到。
using CustomerEntity = CustomerService.Domain.Entities.Customer;

namespace CustomerService.Application.Features.Customer.Admin;

/// <summary>后台客户列表处理器。</summary>
public sealed class QueryAdminCustomersHandler
    : IRequestHandler<QueryAdminCustomersCommand, ApiResponse<PagedResult<AdminCustomerDto>>>
{
    private readonly ICustomerRepository _customers;

    /// <summary>构造处理器。</summary>
    /// <param name="customers">客户仓储。</param>
    public QueryAdminCustomersHandler(ICustomerRepository customers) => _customers = customers;

    /// <inheritdoc />
    public async Task<ApiResponse<PagedResult<AdminCustomerDto>>> Handle(
        QueryAdminCustomersCommand request, CancellationToken ct)
    {
        var (items, total) = await _customers.PageForAdminAsync(
            request.Status, request.Keyword, request.Page, request.PageSize, ct).ConfigureAwait(false);

        return ApiResults.Ok(new PagedResult<AdminCustomerDto>(
            items.Select(AdminCustomerAssembler.Build).ToList(),
            total, request.Page, request.PageSize));
    }
}

/// <summary>后台客户详情处理器。</summary>
public sealed class QueryAdminCustomerDetailHandler
    : IRequestHandler<QueryAdminCustomerDetailCommand, ApiResponse<AdminCustomerDto>>
{
    private readonly ICustomerRepository _customers;

    /// <summary>构造处理器。</summary>
    /// <param name="customers">客户仓储。</param>
    public QueryAdminCustomerDetailHandler(ICustomerRepository customers) => _customers = customers;

    /// <inheritdoc />
    public async Task<ApiResponse<AdminCustomerDto>> Handle(
        QueryAdminCustomerDetailCommand request, CancellationToken ct)
    {
        var customer = await _customers.GetByIdAsync(request.CustomerId, ct).ConfigureAwait(false);

        if (customer is null)
        {
            return ApiResults.Fail<AdminCustomerDto>(BaseApiResponseCode.NotFound, "客户不存在");
        }

        return ApiResults.Ok(AdminCustomerAssembler.Build(customer));
    }
}

/// <summary>启用 / 停用客户处理器。</summary>
public sealed class ChangeCustomerStatusHandler
    : IRequestHandler<ChangeCustomerStatusCommand, ApiResponse>
{
    private readonly ICustomerRepository _customers;

    /// <summary>构造处理器。</summary>
    /// <param name="customers">客户仓储。</param>
    public ChangeCustomerStatusHandler(ICustomerRepository customers) => _customers = customers;

    /// <inheritdoc />
    /// <remarks>
    /// 条件更新带上**期望的当前状态**：两个运营同时点「停用 / 启用」时，
    /// 只有一个能改成功，另一个拿到 0 就回「状态已变更」而不是也报成功——
    /// 否则界面显示的结果与运营以为的结果可能相反。
    /// </remarks>
    public async Task<ApiResponse> Handle(ChangeCustomerStatusCommand request, CancellationToken ct)
    {
        var customer = await _customers.GetByIdAsync(request.CustomerId, ct).ConfigureAwait(false);

        if (customer is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "客户不存在");
        }

        if (customer.Status == request.Status)
        {
            // 目标状态就是当前状态：幂等返回成功，不当成错误
            return ApiResponseFactory.Ok();
        }

        var affected = await _customers
            .TryChangeStatusAsync(request.CustomerId, customer.Status, request.Status, ct)
            .ConfigureAwait(false);

        if (affected == 0)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.OrderStateInvalid, "客户状态已变更，请刷新后重试");
        }

        return ApiResponseFactory.Ok();
    }
}

/// <summary>客户 DTO 组装器。</summary>
/// <remarks>
/// 时间**不下发 DateTime**，而是已经格式化好的字符串。
/// 后端下 DateTime 会被序列化成 ISO 串（2026-10-02T06:30:00Z），
/// 界面就会露出原始数据；要本地化就得让每个页面各写一遍格式化，
/// 而各写一遍正是「界面出现 ISO 时间串」的根源。
/// </remarks>
internal static class AdminCustomerAssembler
{
    internal static AdminCustomerDto Build(CustomerEntity c)
        => new(
            c.Id, c.CustomerName, c.NickName, c.Phone, c.Avatar,
            c.Gender, GenderName(c.Gender),
            c.Status, CustomerStatuses.NameOf(c.Status),
            c.LastLoginAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            c.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));

    private static string GenderName(int gender)
        => gender switch
        {
            1 => "男",
            2 => "女",
            _ => "未知"
        };
}
