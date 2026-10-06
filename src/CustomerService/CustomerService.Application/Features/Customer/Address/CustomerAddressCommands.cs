using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;

namespace CustomerService.Application.Features.Customer.Address;

/// <summary>地址视图。</summary>
/// <param name="AddressId">地址 Id。</param>
/// <param name="ConsigneeName">收货人。</param>
/// <param name="ConsigneePhone">收货手机号。</param>
/// <param name="ProvinceCode">省级编码。</param>
/// <param name="CityCode">市级编码。</param>
/// <param name="DistrictCode">区县编码。</param>
/// <param name="RegionPath">省市区名称路径（斜杠分隔），列表直接展示。</param>
/// <param name="DetailAddress">详细地址。</param>
/// <param name="IsDefault">是否默认地址。</param>
/// <param name="Label">标签，如「家」「公司」。</param>
public sealed record CustomerAddressDto(
    string AddressId, string ConsigneeName, string ConsigneePhone,
    string ProvinceCode, string CityCode, string DistrictCode,
    string RegionPath, string DetailAddress, bool IsDefault, string Label);

/// <summary>查自己的地址簿。</summary>
/// <param name="CustomerId">客户 Id；经网关时必须与令牌一致。</param>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryCustomerAddressesCommand(long CustomerId, int Page = 1, int PageSize = 20)
    : IRequest<ApiResponse<PagedResult<CustomerAddressDto>>>;

/// <summary>新增地址。</summary>
/// <param name="CustomerId">客户 Id；经网关时必须与令牌一致。</param>
/// <param name="ConsigneeName">收货人，1-64 字符。</param>
/// <param name="ConsigneePhone">收货手机号，11 位。</param>
/// <param name="ProvinceCode">省级编码。</param>
/// <param name="CityCode">市级编码。</param>
/// <param name="DistrictCode">区县编码。</param>
/// <param name="RegionPath">省市区名称路径，如「浙江省/杭州市/西湖区」。</param>
/// <param name="DetailAddress">详细地址，1-255 字符。</param>
/// <param name="IsDefault">是否设为默认。</param>
/// <param name="Label">标签，可空。</param>
public record CreateCustomerAddressCommand(
    long CustomerId, string ConsigneeName, string ConsigneePhone,
    string ProvinceCode, string CityCode, string DistrictCode,
    string RegionPath, string DetailAddress, bool IsDefault = false, string Label = "")
    : IRequest<ApiResponse<string>>, ICustomerAddressSpec;

/// <summary>改地址。</summary>
/// <param name="CustomerId">客户 Id；经网关时必须与令牌一致。</param>
/// <param name="AddressId">地址 Id，必须是自己的。</param>
/// <param name="ConsigneeName">收货人。</param>
/// <param name="ConsigneePhone">收货手机号。</param>
/// <param name="ProvinceCode">省级编码。</param>
/// <param name="CityCode">市级编码。</param>
/// <param name="DistrictCode">区县编码。</param>
/// <param name="RegionPath">省市区名称路径。</param>
/// <param name="DetailAddress">详细地址。</param>
/// <param name="IsDefault">是否设为默认。</param>
/// <param name="Label">标签。</param>
public record UpdateCustomerAddressCommand(
    long CustomerId, long AddressId, string ConsigneeName, string ConsigneePhone,
    string ProvinceCode, string CityCode, string DistrictCode,
    string RegionPath, string DetailAddress, bool IsDefault = false, string Label = "")
    : IRequest<ApiResponse>, ICustomerAddressSpec;

/// <summary>删地址。</summary>
/// <param name="CustomerId">客户 Id；经网关时必须与令牌一致。</param>
/// <param name="AddressId">地址 Id，必须是自己的。</param>
public record DeleteCustomerAddressCommand(long CustomerId, long AddressId)
    : IRequest<ApiResponse>;

/// <summary>设为默认地址。</summary>
/// <param name="CustomerId">客户 Id；经网关时必须与令牌一致。</param>
/// <param name="AddressId">地址 Id，必须是自己的。</param>
public record SetDefaultCustomerAddressCommand(long CustomerId, long AddressId)
    : IRequest<ApiResponse>;

/// <summary>地址的可校验字段（新增与编辑共用同一套规则）。</summary>
/// <remarks>
/// 新增与编辑是同一个表单的两条路径，规则必须一致 —— 各写一份的结果是缺陷只会从松的那侧漏出来
/// （券模板、券活动、营销活动都栽过这个跟头）。
///
/// <para><b>必须用属性表达式</b>（<c>x =&gt; x.ConsigneeName</c>）：FluentValidation
/// 从表达式里提取属性名做错误键，写成方法调用（<c>x =&gt; nameOf(x)</c>）提不出来，
/// 错误键会变成空字符串，前端就没法把提示挂到对应输入框下面。</para>
/// </remarks>
public interface ICustomerAddressSpec
{
    /// <summary>收货人。</summary>
    string ConsigneeName { get; }

    /// <summary>收货手机号。</summary>
    string ConsigneePhone { get; }

    /// <summary>省市区名称路径。</summary>
    string RegionPath { get; }

    /// <summary>详细地址。</summary>
    string DetailAddress { get; }

    /// <summary>地址标签。</summary>
    string Label { get; }
}

/// <summary>地址字段规则（新增与编辑共用）。</summary>
/// <typeparam name="T">命令类型，实现 <see cref="ICustomerAddressSpec"/>。</typeparam>
internal sealed class CustomerAddressRules<T> : AbstractValidator<T>
    where T : ICustomerAddressSpec
{
    /// <summary>构造规则集。</summary>
    internal CustomerAddressRules()
    {
        // WithMessage 只作用于紧挨着它的那一个校验器，两条规则各写各的文案
        RuleFor(x => x.ConsigneeName).NotEmpty().WithMessage("收货人必填");
        RuleFor(x => x.ConsigneeName).MaximumLength(64).WithMessage("收货人不超过 64 个字符");

        // 手机号只认 11 位大陆号段：地址簿是唯一能改「收货手机号」的地方，
        // 这里放过去，后面每一次下单都会带着一个打不通的号码。
        RuleFor(x => x.ConsigneePhone).Matches(@"^1[3-9]\d{9}$")
            .WithMessage("收货手机号格式不正确");

        RuleFor(x => x.RegionPath).NotEmpty().WithMessage("请选择所在地区");
        RuleFor(x => x.RegionPath).MaximumLength(255).WithMessage("所在地区文本过长");
        RuleFor(x => x.DetailAddress).NotEmpty().WithMessage("详细地址必填");
        RuleFor(x => x.DetailAddress).MaximumLength(255).WithMessage("详细地址不超过 255 个字符");
        RuleFor(x => x.Label).MaximumLength(16).WithMessage("地址标签不超过 16 个字符");
    }
}

/// <summary>新增地址校验。</summary>
public sealed class CreateCustomerAddressValidator : AbstractValidator<CreateCustomerAddressCommand>
{
    /// <summary>构造校验器。</summary>
    public CreateCustomerAddressValidator()
    {
        RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户信息不正确");
        Include(new CustomerAddressRules<CreateCustomerAddressCommand>());
    }
}

/// <summary>改地址校验。</summary>
public sealed class UpdateCustomerAddressValidator : AbstractValidator<UpdateCustomerAddressCommand>
{
    /// <summary>构造校验器。</summary>
    public UpdateCustomerAddressValidator()
    {
        RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户信息不正确");
        RuleFor(x => x.AddressId).GreaterThan(0).WithMessage("地址信息不正确");
        Include(new CustomerAddressRules<UpdateCustomerAddressCommand>());
    }
}

/// <summary>地址分页校验。</summary>
public sealed class QueryCustomerAddressesValidator : AbstractValidator<QueryCustomerAddressesCommand>
{
    /// <summary>构造校验器。</summary>
    public QueryCustomerAddressesValidator()
    {
        RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户信息不正确");
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须大于 0");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("每页条数必须在 1 ~ 100 之间");
    }
}

