using Collaboration.Domain.Common;
using MediatR;

namespace MerchantPlatformService.Application.Features.Merchant;

/// <summary>创建商户。</summary>
/// <remarks>
/// <b>新建默认停用 + 待审核</b>（规格 5.2）：商户资质没审过之前不能对外运营。
/// <c>MerchantNo</c> 与 <c>AuditStatus</c> 都是系统生成，表单里不传。
/// </remarks>
/// <param name="MerchantName">商户 / 店铺名称，同平台内唯一。</param>
/// <param name="PlatformId">所属平台，必须是启用状态的平台。</param>
/// <param name="ContactName">联系人。</param>
/// <param name="ContactPhone">联系电话。</param>
/// <param name="Logo">店铺 Logo。</param>
/// <param name="Description">店铺简介，C 端店铺页展示。</param>
/// <param name="Status">1 启用 / 2 停用。默认 2。</param>
/// <param name="Remark">备注。</param>
public record CreateMerchantCommand(
    string MerchantName,
    long PlatformId,
    string ContactName,
    string ContactPhone,
    string Logo = "",
    string Description = "",
    int Status = 2,
    string Remark = "") : IRequest<ApiResponse<long>>;

/// <summary>编辑商户。</summary>
/// <remarks>
/// 审核状态<b>不重置</b>：已通过商户改个名字不需要重新审核；
/// 已拒绝商户改完信息由「重新提交」动作把它打回待审核（规格 5.2）。
/// </remarks>
/// <param name="MerchantId">商户 Id。</param>
/// <param name="MerchantName">商户名称。</param>
/// <param name="PlatformId">所属平台 Id。<b>不允许改</b>，传 0 表示不改。</param>
/// <param name="ContactName">联系人。</param>
/// <param name="ContactPhone">联系电话。</param>
/// <param name="Logo">店铺 Logo。</param>
/// <param name="Description">店铺简介。</param>
/// <param name="Status">1 启用 / 2 停用。</param>
/// <param name="Remark">备注。</param>
public record UpdateMerchantCommand(
    long MerchantId,
    string MerchantName,
    long PlatformId,
    string ContactName,
    string ContactPhone,
    string Logo = "",
    string Description = "",
    int Status = 2,
    string Remark = "") : IRequest<ApiResponse>;

/// <summary>商户审核。</summary>
/// <param name="MerchantId">商户 Id。</param>
/// <param name="AuditStatus">审核结论：<b>只能 20 已通过 或 90 已拒绝</b>。</param>
/// <param name="AuditRemark">审核意见 / 拒绝原因，<b>拒绝时必填</b>。</param>
/// <remarks>
/// <b>刻意不接受审核人字段</b>：审核人由 Handler 从令牌租户上下文取。
/// 之前这里有 AuditorId / AuditorName 且直接采信请求体 ——
/// 调用方可以自称任意审核人，而「审核人 + 审核意见」是商户准入的审计凭据。
/// 字段留着就等于留了个静默的伪造入口：不传时行为正确，
/// 一旦有人（或某个脚本）传了，审计记录就被改写了而接口照常返回成功。
/// </remarks>
public record AuditMerchantCommand(
    long MerchantId,
    int AuditStatus,
    string AuditRemark = "") : IRequest<ApiResponse<AuditMerchantResult>>;

/// <summary>删除商户。<b>有商品或订单时禁止，只能停用</b>。</summary>
/// <param name="MerchantId">商户 Id。</param>
public record DeleteMerchantCommand(long MerchantId) : IRequest<ApiResponse>;

/// <summary>已拒绝商户修改资料后重新提交审核。</summary>
/// <remarks>
/// 单独一个动作而不是复用「编辑」：编辑<b>不重置审核状态</b>（规格 5.2），
/// 而重新提交必须把状态从 90 打回 10。混进编辑里就会出现「改个电话就要重新审」的荒唐行为。
/// </remarks>
/// <param name="MerchantId">商户 Id。</param>
public record ResubmitMerchantCommand(long MerchantId) : IRequest<ApiResponse>;

/// <summary>
/// 启用 / 停用商户。
/// </summary>
/// <remarks>
/// 独立命令而不是复用 <see cref="UpdateMerchantCommand"/>：
/// 编辑要带一整套必填字段（名称 / 联系人 / 电话），而列表上的「停用」只有两个入参。
/// 复用编辑的话，前端得先把整行读回来再原样提交，多一次往返，
/// 而且中途别人改了名称就会互相覆盖 —— 启停这种高频轻动作必须自己只写一个字段。
/// </remarks>
/// <param name="MerchantId">商户 Id。</param>
/// <param name="Status">1 启用 / 2 停用。</param>
public record ChangeMerchantStatusCommand(long MerchantId, int Status) : IRequest<ApiResponse>;

/// <summary>商户列表。</summary>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
/// <param name="Keyword">名称 / 编号模糊匹配。</param>
/// <param name="AuditStatus">审核状态，0 表示不限。</param>
/// <param name="Status">启停状态，0 表示不限。</param>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryMerchantsCommand(
    long PlatformId = 0,
    string Keyword = "",
    int AuditStatus = 0,
    int Status = 0,
    int Page = 1,
    int PageSize = 20) : IRequest<ApiResponse<PagedMerchantDtos>>;

/// <summary>商户下拉框数据。</summary>
/// <param name="PlatformId">平台 Id，0 表示不限平台。</param>
public record QueryMerchantOptionsQuery(long PlatformId = 0)
    : IRequest<ApiResponse<List<MerchantOptionDto>>>;

/// <summary>审核结果。</summary>
/// <param name="MerchantId">商户 Id。</param>
/// <param name="AuditStatusName">审核状态中文名。</param>
/// <param name="OffShelvedCount">被连带下架的商品数（拒绝时才有值）。</param>
/// <param name="OffShelveSynced">下架是否已同步搜索索引。</param>
public sealed record AuditMerchantResult(
    long MerchantId, string AuditStatusName, int OffShelvedCount, bool OffShelveSynced);
