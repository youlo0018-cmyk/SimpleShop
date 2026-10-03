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