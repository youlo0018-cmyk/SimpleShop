using Collaboration.Domain.Common;
using Collaboration.Domain.MediatR;
using MediatR;

namespace MerchantPlatformService.Application.Features.Platform;

/// <summary>创建平台。</summary>
/// <param name="PlatformName">平台名称，全局唯一。</param>
/// <param name="PlatformCode">平台编码，6 位字母。<b>创建后只读</b>。</param>
/// <param name="ContactName">联系人。</param>
/// <param name="ContactPhone">联系电话。</param>
/// <param name="Logo">平台 Logo。</param>
/// <param name="MallName">商城名称。</param>
/// <param name="Notice">首页公告。</param>
/// <param name="PrimaryColor">主题色 <c>#RRGGBB</c>。</param>
/// <param name="TabColor">TabBar 选中色。</param>
/// <param name="BackgroundColor">页面背景色。</param>
/// <param name="ShippingFee">运费，仅对实物快递收取。</param>
/// <param name="FreeShippingThreshold">满额包邮门槛，0 表示不启用。</param>
/// <param name="Status">1 启用 / 2 停用。</param>
/// <param name="Remark">备注。</param>
/// <remarks>
/// <b>仅超级管理员可执行</b>：平台是整个系统的租户顶层，谁能建平台等于谁能在系统里开一块新地盘。
/// 这条规则不能只靠「别把 platform:create 勾给平台角色」——权限点是运行时可配置的实体，
/// 某天有人在权限管理界面把它勾给了一个平台角色，网关就会放行。
/// 所以这里挂 ISuperAdminOnly：**不看权限点，只看租户身份**，超管之外一律 403。
/// </remarks>
public record CreatePlatformCommand(
    string PlatformName,
    string PlatformCode,
    string ContactName,
    string ContactPhone,
    string Logo = "",
    string MallName = "",
    string Notice = "",
    string PrimaryColor = "#0071e3",
    string TabColor = "#0071e3",
    string BackgroundColor = "#f5f5f7",
    decimal ShippingFee = 0m,
    decimal FreeShippingThreshold = 0m,
    int Status = 1,
    string Remark = "") : IRequest<ApiResponse<long>>, ISuperAdminOnly
{
    /// <inheritdoc />
    public string AuditNote => "新建平台";
}

/// <summary>编辑平台。<c>PlatformCode</c> 传什么都无效，一律保留原值。</summary>
/// <param name="PlatformId">平台 Id。</param>
/// <param name="PlatformName">平台名称。</param>
/// <param name="PlatformCode">平台编码（<b>会被忽略</b>）。</param>
/// <param name="ContactName">联系人。</param>
/// <param name="ContactPhone">联系电话。</param>
/// <param name="Logo">平台 Logo。</param>
/// <param name="MallName">商城名称。</param>
/// <param name="Notice">首页公告。</param>
/// <param name="PrimaryColor">主题色。</param>
/// <param name="TabColor">TabBar 选中色。</param>
/// <param name="BackgroundColor">页面背景色。</param>
/// <param name="ShippingFee">运费。</param>
/// <param name="FreeShippingThreshold">满额包邮门槛。</param>
/// <param name="Status">1 启用 / 2 停用。</param>
/// <param name="Remark">备注。</param>
public record UpdatePlatformCommand(
    long PlatformId,
    string PlatformName,
    string PlatformCode,
    string ContactName,
    string ContactPhone,
    string Logo = "",
    string MallName = "",
    string Notice = "",
    string PrimaryColor = "#0071e3",
    string TabColor = "#0071e3",
    string BackgroundColor = "#f5f5f7",
    decimal ShippingFee = 0m,
    decimal FreeShippingThreshold = 0m,
    int Status = 1,
    string Remark = "") : IRequest<ApiResponse>;

/// <summary>删除平台。<b>有下级商户时禁止删除，只能停用</b>。</summary>
/// <param name="PlatformId">平台 Id。</param>
public record DeletePlatformCommand(long PlatformId) : IRequest<ApiResponse>;

/// <summary>平台列表 / 详情。</summary>
/// <param name="Keyword">名称 / 编码模糊匹配。</param>
/// <param name="Status">状态过滤，0 表示不限。</param>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryPlatformsCommand(
    string Keyword = "", int Status = 0, int Page = 1, int PageSize = 20)
    : IRequest<ApiResponse<PagedPlatformDtos>>;

/// <summary>平台下拉框数据。</summary>
/// <remarks>
/// 单独一个接口而不是从 List 里筛：下拉框要的是<b>全部启用平台</b>且不分页，
/// 而 List 带分页与关键词过滤。后台建商户时要选平台，这里是最频繁的下拉来源。
/// </remarks>
public record QueryPlatformOptionsQuery : IRequest<ApiResponse<List<PlatformOptionDto>>>;

/// <summary>平台下拉项。</summary>
/// <param name="Id">平台 Id。</param>
/// <param name="Name">平台名称。<b>前端下拉直接显示它，不显示 Id</b>。</param>
/// <param name="Code">平台编码。</param>
/// <summary>平台下拉项。</summary>
/// <param name="Id">平台 Id，<b>字符串下发</b>（DATA_SPEC 4.6：雪花 Id 前端必须保持字符串）。</param>
/// <param name="Name">平台名。</param>
/// <param name="Code">平台编码，小程序按它锁平台。</param>
/// <remarks>下拉项统一形状 <c>{ id, name }</c>（4.7），按需追加字段。</remarks>
public sealed record PlatformOptionDto(string Id, string Name, string Code);

/// <summary>平台列表项。</summary>
/// <param name="Id">平台 Id。</param>
/// <param name="PlatformName">平台名称。</param>
/// <param name="PlatformCode">平台编码。</param>
/// <param name="MallName">商城名称。</param>
/// <param name="ContactName">联系人。</param>
/// <param name="ContactPhone">联系电话。</param>
/// <param name="PrimaryColor">主题色。</param>
/// <param name="TabColor">TabBar 选中色。</param>
/// <param name="BackgroundColor">页面背景色。</param>
/// <param name="ShippingFee">运费。</param>
/// <param name="FreeShippingThreshold">满额包邮门槛。</param>
/// <param name="Status">启停状态。</param>
/// <param name="StatusName">启停状态中文名（前端不显示数字枚举）。</param>
/// <param name="MerchantCount">商户数（只读统计）。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="CreatedByName">创建人。</param>
/// <param name="UpdatedAt">最后更新时间。</param>
/// <param name="OperationName">最后操作人。</param>
public sealed record PlatformListDto(
    long Id,
    string PlatformName,
    string PlatformCode,
    string MallName,
    string ContactName,
    string ContactPhone,
    string PrimaryColor,
    string TabColor,
    string BackgroundColor,
    decimal ShippingFee,
    decimal FreeShippingThreshold,
    int Status,
    string StatusName,
    long MerchantCount,
    string CreatedAt,
    string CreatedByName,
    string UpdatedAt,
    string OperationName);

/// <summary>平台分页结果。</summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record PagedPlatformDtos(
    IReadOnlyList<PlatformListDto> Items, long Total, int Page, int PageSize);
