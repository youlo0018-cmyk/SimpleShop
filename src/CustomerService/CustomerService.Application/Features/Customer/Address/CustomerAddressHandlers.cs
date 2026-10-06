using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using CustomerService.Domain.Entities;
using CustomerService.Domain.IRepository;
using MediatR;

namespace CustomerService.Application.Features.Customer.Address;

/// <summary>查自己的地址簿。</summary>
/// <remarks>
/// 归属靠两道防线：<see cref="CustomerScope.Require"/> 挡住「传别人的 customerId」，
/// 仓储上的客户 AOP 再按 <c>customer_id</c> 过滤一次（DATA_SPEC 2.3）。
/// 两道都要有：AOP 只在客户上下文存在时生效，而 C 端接口也可能被内部任务直连。
/// </remarks>
public sealed class QueryCustomerAddressesHandler
    : IRequestHandler<QueryCustomerAddressesCommand, ApiResponse<PagedResult<CustomerAddressDto>>>
{
    private readonly ICustomerAddressRepository _addresses;

    /// <summary>构造处理器。</summary>
    /// <param name="addresses">地址仓储。</param>
    public QueryCustomerAddressesHandler(ICustomerAddressRepository addresses) => _addresses = addresses;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>地址分页，默认地址优先。</returns>
    public async Task<ApiResponse<PagedResult<CustomerAddressDto>>> Handle(
        QueryCustomerAddressesCommand request, CancellationToken ct)
    {
        CustomerScope.Require(request.CustomerId);

        var (items, total) = await _addresses
            .QueryPagedAsync(request.Page, request.PageSize, ct).ConfigureAwait(false);

        return ApiResults.Ok(new PagedResult<CustomerAddressDto>(
            items.Select(ToDto).ToList(), total, request.Page, request.PageSize));
    }

    /// <summary>组装地址视图。</summary>
    /// <param name="address">地址实体。</param>
    /// <returns>地址视图。</returns>
    internal static CustomerAddressDto ToDto(CustomerAddress address) => new(
        address.Id.ToString(),
        address.ConsigneeName,
        address.ConsigneePhone,
        address.ProvinceCode,
        address.CityCode,
        address.DistrictCode,
        address.RegionPath,
        address.DetailAddress,
        address.IsDefault,
        address.Label);
}

/// <summary>新增地址。</summary>
public sealed class CreateCustomerAddressHandler
    : IRequestHandler<CreateCustomerAddressCommand, ApiResponse<string>>
{
    private readonly ICustomerAddressRepository _addresses;

    /// <summary>构造处理器。</summary>
    /// <param name="addresses">地址仓储。</param>
    public CreateCustomerAddressHandler(ICustomerAddressRepository addresses) => _addresses = addresses;

    /// <summary>执行新增。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新地址 Id。</returns>
    /// <remarks>
    /// 第一条地址**自动成为默认**：一条默认都没有的话，结算页不知道该预选哪个，
    /// 用户每次都要多点一次。
    /// </remarks>
    public async Task<ApiResponse<string>> Handle(
        CreateCustomerAddressCommand request, CancellationToken ct)
    {
        CustomerScope.Require(request.CustomerId);

        var (_, total) = await _addresses.QueryPagedAsync(1, 1, ct).ConfigureAwait(false);

        var address = new CustomerAddress
        {
            ConsigneeName = request.ConsigneeName.Trim(),
            ConsigneePhone = request.ConsigneePhone.Trim(),
            ProvinceCode = request.ProvinceCode.Trim(),
            CityCode = request.CityCode.Trim(),
            DistrictCode = request.DistrictCode.Trim(),
            RegionPath = request.RegionPath.Trim(),
            DetailAddress = request.DetailAddress.Trim(),
            // 默认标记交给 SetDefaultAsync 统一处理（它会先清掉其它默认，避免撞部分唯一索引）
            IsDefault = false,
            Label = (request.Label ?? string.Empty).Trim()
        };

        var id = await _addresses.InsertAsync(address, ct).ConfigureAwait(false);

        if (request.IsDefault || total == 0)
        {
            await _addresses.SetDefaultAsync(id, ct).ConfigureAwait(false);
        }

        return ApiResults.Ok(id.ToString(), "地址已保存");
    }
}

/// <summary>改地址。</summary>
public sealed class UpdateCustomerAddressHandler
    : IRequestHandler<UpdateCustomerAddressCommand, ApiResponse>
{
    private readonly ICustomerAddressRepository _addresses;

    /// <summary>构造处理器。</summary>
    /// <param name="addresses">地址仓储。</param>
    public UpdateCustomerAddressHandler(ICustomerAddressRepository addresses) => _addresses = addresses;

    /// <summary>执行更新。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// 查不到就返回 404：仓储的客户 AOP 已按 <c>customer_id</c> 过滤，
    /// 别人的地址在这里等同于「不存在」——**不要**回 403，那等于告诉对方「这个 Id 真实存在」。
    /// </remarks>
    public async Task<ApiResponse> Handle(UpdateCustomerAddressCommand request, CancellationToken ct)
    {
        CustomerScope.Require(request.CustomerId);

        var address = await _addresses.GetByIdAsync(request.AddressId, ct).ConfigureAwait(false);
        if (address is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "地址不存在");
        }

        address.ConsigneeName = request.ConsigneeName.Trim();
        address.ConsigneePhone = request.ConsigneePhone.Trim();
        address.ProvinceCode = request.ProvinceCode.Trim();
        address.CityCode = request.CityCode.Trim();
        address.DistrictCode = request.DistrictCode.Trim();
        address.RegionPath = request.RegionPath.Trim();
        address.DetailAddress = request.DetailAddress.Trim();
        address.Label = (request.Label ?? string.Empty).Trim();

        await _addresses.UpdateAsync(address, ct).ConfigureAwait(false);

        if (request.IsDefault)
        {
            await _addresses.SetDefaultAsync(address.Id, ct).ConfigureAwait(false);
        }

        return ApiResponseFactory.Ok("地址已保存");
    }
}

/// <summary>删地址。</summary>
public sealed class DeleteCustomerAddressHandler
    : IRequestHandler<DeleteCustomerAddressCommand, ApiResponse>
{
    private readonly ICustomerAddressRepository _addresses;

    /// <summary>构造处理器。</summary>
    /// <param name="addresses">地址仓储。</param>
    public DeleteCustomerAddressHandler(ICustomerAddressRepository addresses) => _addresses = addresses;

    /// <summary>执行删除（软删）。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// 删掉的是**默认地址**时，把剩下最新的一条顶上默认：否则用户下次结算会发现一条都没预选，
    /// 而界面上明明还有地址可选。
    /// </remarks>
    public async Task<ApiResponse> Handle(DeleteCustomerAddressCommand request, CancellationToken ct)
    {
        CustomerScope.Require(request.CustomerId);

        var address = await _addresses.GetByIdAsync(request.AddressId, ct).ConfigureAwait(false);
        if (address is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "地址不存在");
        }

        await _addresses.DeleteAsync(request.AddressId, ct).ConfigureAwait(false);

        if (address.IsDefault)
        {
            var (rest, _) = await _addresses.QueryPagedAsync(1, 1, ct).ConfigureAwait(false);
            var next = rest.FirstOrDefault();
            if (next is not null)
            {
                await _addresses.SetDefaultAsync(next.Id, ct).ConfigureAwait(false);
            }
        }

        return ApiResponseFactory.Ok("地址已删除");
    }
}

/// <summary>设为默认地址。</summary>
public sealed class SetDefaultCustomerAddressHandler
    : IRequestHandler<SetDefaultCustomerAddressCommand, ApiResponse>
{
    private readonly ICustomerAddressRepository _addresses;

    /// <summary>构造处理器。</summary>
    /// <param name="addresses">地址仓储。</param>
    public SetDefaultCustomerAddressHandler(ICustomerAddressRepository addresses) => _addresses = addresses;

    /// <summary>执行设置。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(SetDefaultCustomerAddressCommand request, CancellationToken ct)
    {
        CustomerScope.Require(request.CustomerId);

        var affected = await _addresses.SetDefaultAsync(request.AddressId, ct).ConfigureAwait(false);
        return affected == 0
            ? ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "地址不存在")
            : ApiResponseFactory.Ok("已设为默认地址");
    }
}
