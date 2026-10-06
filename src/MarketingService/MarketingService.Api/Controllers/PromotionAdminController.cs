using Collaboration.Domain.Common;
using MarketingService.Application.Features.Promotion;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace MarketingService.Api.Controllers;

/// <summary>营销活动（满减 / 满折 / 满赠）后台接口。</summary>
[ApiController]
[Route("marketing/activities")]
public sealed class PromotionAdminController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public PromotionAdminController(IMediator mediator) => _mediator = mediator;

    /// <summary>新建活动。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回活动 Id。</returns>
    [HttpPost("Create")]
    public Task<ApiResponse<long>> Create([FromBody] CreatePromotionActivityCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>编辑活动。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Update")]
    public Task<ApiResponse> Update([FromBody] UpdatePromotionActivityCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>活动分页。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>活动分页结果，含类型 / 范围 / 状态中文名。</returns>
    [HttpPost("List")]
    public Task<ApiResponse<PagedPromotionResult>> List(
        [FromBody] QueryPromotionActivitiesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>活动详情。</summary>
    /// <param name="activityId">活动 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>活动详情。</returns>
    [HttpGet("Get")]
    public Task<ApiResponse<PromotionActivityDto>> Get([FromQuery] long activityId, CancellationToken ct)
        => _mediator.Send(new GetPromotionActivityCommand(activityId), ct);

    /// <summary>启用 / 停用活动。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("SetStatus")]
    public Task<ApiResponse> SetStatus([FromBody] SetPromotionStatusCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>删除活动（软删）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Delete")]
    public Task<ApiResponse> Delete([FromBody] DeletePromotionActivityCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>活动参与记录（营销效果报表下钻订单明细）。</summary>
    /// <param name="command">查询命令，含活动 Id 与时间范围档位。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当页参与记录（订单号 / 客户 / 折扣额 / 时间）。</returns>
    /// <remarks>
    /// 时间口径与 <c>/reports/Marketing</c> 完全一致（<see cref="Collaboration.Domain.Services.ReportRanges"/>），
    /// 否则下钻出来的行数与报表上的「参与订单数」对不上，运营会以为数据丢了。
    /// </remarks>
    [HttpPost("Records")]
    public Task<ApiResponse<PagedResult<ActivityRecordItem>>> Records(
        [FromBody] QueryActivityRecordsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>分组批量试算（商品列表页专用）。<b>每组独立算，组间互不影响。</b></summary>
    /// <param name="command">命令；Groups 每组是一个 SKU。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>与 Groups 一一对应的试算结果。</returns>
    /// <remarks>
    /// 商品列表页必须用这个而不是 <c>FinalPrice</c>：后者把传进来的所有行当成**一单**，
    /// 门槛按合计判定、再把优惠摊到各行，于是「满 100 减 20」会把 200 元商品显示成 186.67——
    /// 而用户真买那一件时其实是 200。商品卡上的价必须等于他自己下单时的价。
    /// </remarks>
    [HttpPost("FinalPriceBatch")]
    public Task<ApiResponse<IReadOnlyList<FinalPriceDto>>> FinalPriceBatch(
        [FromBody] CalculateFinalPriceBatchCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>到手价试算（游客与 C 端共用）。</summary>
    /// <param name="command">命令；CustomerId 传 0 即游客。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>原价 / 活动优惠 / 券优惠 / 到手价与逐行拆分，<b>不含运费</b>。</returns>
    /// <remarks>
    /// 游客传 <c>customerId = 0</c> 即可，无需令牌——游客也能看到活动价，
    /// 只是看不到券价（游客没有券包，见 BUSINESS.md 11.5）。
    /// </remarks>
    [HttpPost("FinalPrice")]
    public Task<ApiResponse<FinalPriceDto>> FinalPrice(
        [FromBody] CalculateFinalPriceCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
