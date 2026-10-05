using Collaboration.Domain.Common;
using MediatR;
using MerchantPlatformService.Application.Features.Merchant;
using Microsoft.AspNetCore.Mvc;

namespace MerchantPlatformService.Api.Controllers;

/// <summary>商户管理接口。</summary>
/// <remarks>
/// 权限点：<c>merchant:create</c> / <c>merchant:update</c> / <c>merchant:audit</c>（DATA_SPEC 5.2、5.3）。
/// </remarks>
[ApiController]
[Route("merchants")]
public sealed class MerchantController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public MerchantController(IMediator mediator) => _mediator = mediator;

    /// <summary>创建商户。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商户 Id。</returns>
    /// <remarks>
    /// 新建商户<b>固定为停用 + 待审核</b>：商户资质没过审之前不能对外运营，
    /// 这不是可选项而是合规要求。
    /// </remarks>
    [HttpPost("Create")]
    public Task<ApiResponse<long>> Create(
        [FromBody] CreateMerchantCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>编辑商户。<b>不重置审核状态</b>，归属平台也不可改。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Update")]
    public Task<ApiResponse> Update(
        [FromBody] UpdateMerchantCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>审核商户。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>审核结果，含被连带下架的商品数。</returns>
    /// <remarks>
    /// <b>拒绝会连带下架该商户全部已上架商品并同步搜索索引</b>：
    /// 商品资质依赖商户资质，不同步索引就会出现「商品页看不到但搜索搜得到」。
    /// </remarks>
    [HttpPost("Audit")]
    public Task<ApiResponse<AuditMerchantResult>> Audit(
        [FromBody] AuditMerchantCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>删除商户。<b>已通过审核的商户不能删除，只能停用。</b></summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Delete")]
    public Task<ApiResponse> Delete(
        [FromBody] DeleteMerchantCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>已拒绝商户重新提交审核。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// 单独一个动作而不是复用「编辑」：编辑<b>不重置审核状态</b>，
    /// 而重新提交必须把状态从「已拒绝」打回「待审核」。
    /// </remarks>
    [HttpPost("Resubmit")]
    public Task<ApiResponse> Resubmit(
        [FromBody] ResubmitMerchantCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>启用 / 停用商户。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// <b>审核通过时已经自动启用</b>，这里主要用于事后停业与恢复营业。
    /// 启用时若商户尚未通过审核会被拒绝 —— 审核状态与启停状态是两个字段，
    /// 但「小程序只展示审核通过的商户」意味着启用了没审过的商户会看起来生效、实际不可见。
    /// </remarks>
    [HttpPost("ChangeStatus")]
    public Task<ApiResponse> ChangeStatus(
        [FromBody] ChangeMerchantStatusCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>商户列表。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商户分页，含平台名与状态中文名。</returns>
    [HttpPost("List")]
    public Task<ApiResponse<PagedMerchantDtos>> List(
        [FromBody] QueryMerchantsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>商户下拉框数据。</summary>
    /// <param name="platformId">平台 Id，0 表示全部平台。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>启用商户的下拉项；<b>直接返回名称供下拉显示</b>。</returns>
    [HttpGet("Options")]
    public async Task<ActionResult<ApiResponse<List<MerchantOptionDto>>>> Options(
        [FromQuery] long platformId, CancellationToken ct)
    {
        var result = await _mediator.Send(new QueryMerchantOptionsQuery(platformId), ct);
        return Ok(result);
    }
}
