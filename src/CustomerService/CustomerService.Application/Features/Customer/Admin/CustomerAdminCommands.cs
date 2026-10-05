using Collaboration.Domain.Common;
using MediatR;

namespace CustomerService.Application.Features.Customer.Admin;

/// <summary>后台客户列表。</summary>
/// <param name="Status">状态，0 表示不限。</param>
/// <param name="Keyword">登录名 / 昵称 / 手机号模糊匹配。</param>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryAdminCustomersCommand(
    int Status = 0, string Keyword = "", int Page = 1, int PageSize = 20)
    : IRequest<ApiResponse<PagedResult<AdminCustomerDto>>>;

/// <summary>后台客户详情。</summary>
/// <param name="CustomerId">客户 Id。</param>
public record QueryAdminCustomerDetailCommand(long CustomerId)
    : IRequest<ApiResponse<AdminCustomerDto>>;

/// <summary>启用 / 停用客户。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="Status">目标状态：1 正常 / 2 停用。</param>
public record ChangeCustomerStatusCommand(long CustomerId, int Status)
    : IRequest<ApiResponse>;

/// <summary>后台客户列表行 / 详情。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="CustomerNo">客户唯一编码，后台按它检索比按手机号更稳（客户可能换号）。</param>
/// <param name="CustomerName">登录名。</param>
/// <param name="NickName">昵称。</param>
/// <param name="Phone">手机号。</param>
/// <param name="Avatar">头像地址。</param>
/// <param name="Gender">性别：0 未知 / 1 男 / 2 女。</param>
/// <param name="GenderName">性别中文名。</param>
/// <param name="Status">账号状态。</param>
/// <param name="StatusName">状态中文名。</param>
/// <param name="LastLoginAt">最后登录时间，未登录为 null。</param>
/// <param name="CreatedAt">注册时间 UTC。</param>
/// <remarks>
/// 后台**不脱敏手机号**：客服接到客户电话时需要按号码找人，脱敏了就等于没法用。
/// 这与「界面不出现原始数据」不冲突 —— 手机号本身是业务字段，
/// 不是枚举数字 / 时间戳 / JSON 原文。
/// </remarks>
public sealed record AdminCustomerDto(
    long CustomerId, string CustomerNo, string CustomerName, string NickName, string Phone, string Avatar,
    int Gender, string GenderName, int Status, string StatusName,
    string? LastLoginAt, string CreatedAt);
