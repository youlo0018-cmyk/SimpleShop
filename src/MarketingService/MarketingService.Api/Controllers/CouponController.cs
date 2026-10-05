using Collaboration.Domain.Common;
using MarketingService.Application.Features.Coupon;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace MarketingService.Api.Controllers;

/// <summary>券：C 端领券 / 结算试算，以及供订单链路的占券、核销、回退。</summary>
[ApiController]
[Route("coupons")]
public sealed class CouponController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public CouponController(IMediator mediator) => _mediator = mediator;

    /// <summary>领券。</summary>
    /// <param name="command">领券命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回券码列表。</returns>
    [HttpPost("Claim")]
    public Task<ApiResponse<ClaimCouponResult>> Claim([FromBody] ClaimCouponCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>查询当前可领取的券活动。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可领取活动列表。</returns>
    [HttpPost("Available")]
    public Task<ApiResponse<List<CouponActivityItem>>> Available(CancellationToken ct)
        => _mediator.Send(new QueryAvailableCouponsCommand(), ct);

    /// <summary>查询当前客户自己的券包。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>券包分页。</returns>
    [HttpPost("My")]
    public Task<ApiResponse<PagedResult<CouponRecordItem>>> My(
        [FromBody] QueryMyCouponsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>结算试算：列出所有可用券及各自优惠额，并标出最优。</summary>
    /// <param name="command">试算命令，CustomerId 传 0 表示游客（游客不计券）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可用券列表与最优券。</returns>
    [HttpPost("Settle")]
    public Task<ApiResponse<SettleCouponResult>> Settle([FromBody] SettleCouponsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>占券（下单时锁定）。</summary>
    /// <param name="command">占券命令，CouponId 传 0 表示自动选最优券。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回实际优惠金额。</returns>
    /// <remarks>
    /// <b>没占到券不算失败</b>：客户可能没券、或券都够不着门槛，此时只是不优惠。
    /// 返回 400 会让订单服务把「没券」当成「券系统故障」而中断下单。
    /// </remarks>
    [HttpPost("Occupy")]
    public Task<ApiResponse<CouponOccupyResult>> Occupy([FromBody] OccupyCouponCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>核销券（支付成功）。</summary>
    /// <param name="command">核销命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Consume")]
    public Task<ApiResponse<CouponOccupyResult>> Consume([FromBody] ConsumeCouponCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>回退占券（取消 / 超时关单）。</summary>
    /// <param name="command">回退命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Release")]
    public Task<ApiResponse<CouponOccupyResult>> Release([FromBody] ReleaseCouponCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
