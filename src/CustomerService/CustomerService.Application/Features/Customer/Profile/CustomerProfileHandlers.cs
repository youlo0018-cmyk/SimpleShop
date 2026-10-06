using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using CustomerService.Domain.IRepository;
using MediatR;
// 命名空间段 Features.Customer 会遮蔽同名实体 Customer（CS0118），用别名绕开
using CustomerEntity = CustomerService.Domain.Entities.Customer;

namespace CustomerService.Application.Features.Customer.Profile;

/// <summary>查自己的资料。</summary>
public sealed class QueryCustomerProfileHandler
    : IRequestHandler<QueryCustomerProfileCommand, ApiResponse<CustomerProfileDto>>
{
    private readonly ICustomerRepository _customers;

    /// <summary>构造处理器。</summary>
    /// <param name="customers">客户仓储。</param>
    public QueryCustomerProfileHandler(ICustomerRepository customers) => _customers = customers;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>客户资料。</returns>
    public async Task<ApiResponse<CustomerProfileDto>> Handle(
        QueryCustomerProfileCommand request, CancellationToken ct)
    {
        // 防 IDOR：客户令牌存在时以令牌里的客户为准（与订单 / 券 / 积分同一口径）
        var customerId = CustomerScope.Require(request.CustomerId);

        var customer = await _customers.GetByIdAsync(customerId, ct).ConfigureAwait(false);
        return customer is null
            ? ApiResults.Fail<CustomerProfileDto>(BaseApiResponseCode.NotFound, "客户不存在")
            : ApiResults.Ok(ToDto(customer));
    }

    /// <summary>组装资料视图。</summary>
    /// <param name="customer">客户实体。</param>
    /// <returns>资料视图。</returns>
    internal static CustomerProfileDto ToDto(CustomerEntity customer) => new(
        customer.Id.ToString(),
        customer.CustomerNo,
        customer.CustomerName,
        // 手机号打码下发：C 端展示够用，完整号码不必要地到处传只会扩大泄露面
        Mask(customer.Phone),
        customer.NickName,
        customer.Avatar,
        customer.Gender,
        CustomerGenders.NameOf(customer.Gender),
        customer.Birthday?.ToString("yyyy-MM-dd") ?? string.Empty);

    /// <summary>手机号打码：138****8000。</summary>
    /// <param name="phone">原始手机号。</param>
    /// <returns>打码后的手机号；长度不足时原样返回。</returns>
    private static string Mask(string phone)
        => phone.Length < 7 ? phone : $"{phone[..3]}****{phone[^4..]}";
}

/// <summary>改自己的资料。</summary>
public sealed class UpdateCustomerProfileHandler
    : IRequestHandler<UpdateCustomerProfileCommand, ApiResponse<CustomerProfileDto>>
{
    private readonly ICustomerRepository _customers;

    /// <summary>构造处理器。</summary>
    /// <param name="customers">客户仓储。</param>
    public UpdateCustomerProfileHandler(ICustomerRepository customers) => _customers = customers;

    /// <summary>执行更新。</summary>
    /// <param name="request">更新命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>更新后的资料。</returns>
    /// <remarks>
    /// 只改展示字段：登录名 / 手机号 / 密码都不在这里 —— 它们要么是唯一键、
    /// 要么需要额外验证，混在「改昵称」这种入口里迟早出问题。
    /// </remarks>
    public async Task<ApiResponse<CustomerProfileDto>> Handle(
        UpdateCustomerProfileCommand request, CancellationToken ct)
    {
        var customerId = CustomerScope.Require(request.CustomerId);

        var customer = await _customers.GetByIdAsync(customerId, ct).ConfigureAwait(false);
        if (customer is null)
        {
            return ApiResults.Fail<CustomerProfileDto>(BaseApiResponseCode.NotFound, "客户不存在");
        }

        customer.NickName = request.NickName.Trim();
        customer.Avatar = (request.Avatar ?? string.Empty).Trim();
        customer.Gender = request.Gender;
        customer.Birthday = request.Birthday;

        await _customers.UpdateAsync(customer, ct).ConfigureAwait(false);
        return ApiResults.Ok(QueryCustomerProfileHandler.ToDto(customer), "资料已保存");
    }
}
