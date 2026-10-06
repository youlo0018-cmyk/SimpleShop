namespace MerchantPlatformService.Application.Features.Merchant;

/// <summary>商户列表项。</summary>
/// <param name="Id">商户 Id。</param>
/// <param name="MerchantName">商户名称。</param>
/// <param name="MerchantNo">商户编号（平台编码 + 雪花 Id）。</param>
/// <param name="PlatformId">所属平台 Id。</param>
/// <param name="PlatformName">所属平台名称，<b>前端直接显示它，不显示 Id</b>。</param>
/// <param name="ContactName">联系人。</param>
/// <param name="ContactPhone">联系电话。</param>
/// <param name="Logo">店铺 Logo。</param>
/// <param name="Description">店铺简介。</param>
/// <param name="Status">启停状态。</param>
/// <param name="StatusName">启停状态中文名。</param>
/// <param name="AuditStatus">审核状态。</param>
/// <param name="AuditStatusName">审核状态中文名。</param>
/// <param name="AuditRemark">审核意见 / 拒绝原因。</param>
/// <param name="AuditedAt">审核时间，未审核为空。</param>
/// <param name="AuditorName">审核人。</param>
/// <param name="Rating">店铺评分，0 表示还没有评价。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="CreatedByName">创建人。</param>
/// <param name="UpdatedAt">最后更新时间。</param>
/// <param name="OperationName">最后操作人。</param>
public sealed record MerchantListDto(
    long Id,
    string MerchantName,
    string MerchantNo,
    long PlatformId,
    string PlatformName,
    string ContactName,
    string ContactPhone,
    string Logo,
    string Description,
    int Status,
    string StatusName,
    int AuditStatus,
    string AuditStatusName,
    string AuditRemark,
    string AuditedAt,
    string AuditorName,
    decimal Rating,
    string CreatedAt,
    string CreatedByName,
    string UpdatedAt,
    string OperationName);

/// <summary>商户分页结果。</summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record PagedMerchantDtos(
    IReadOnlyList<MerchantListDto> Items, long Total, int Page, int PageSize);

/// <summary>商户下拉项。</summary>
/// <param name="Id">商户 Id。</param>
/// <param name="Name">商户名称，<b>前端下拉直接显示它</b>。</param>
/// <param name="PlatformId">所属平台 Id。</param>
/// <summary>商户下拉项。</summary>
/// <param name="Id">商户 Id，<b>字符串下发</b>（4.6）。</param>
/// <param name="Name">店铺名。</param>
/// <param name="PlatformId">所属平台 Id，字符串下发；平台切换时前端据此联动过滤。</param>
/// <remarks>下拉项统一形状 <c>{ id, name }</c>（4.7），按需追加字段。</remarks>
public sealed record MerchantOptionDto(string Id, string Name, string PlatformId);
