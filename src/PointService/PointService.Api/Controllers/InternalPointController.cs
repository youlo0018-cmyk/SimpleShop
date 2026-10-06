using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using PointService.Application.Features.Operations;

namespace PointService.Api.Controllers;

/// <summary>积分内部接口，供注册 / 订单 / 支付 / 退款链路调用。网关不路由 /internal 前缀。</summary>
/// <remarks>
/// 与库存同构：下单链路是「占券 → 锁积分 → 锁库存 → 落单」，
/// 任一步失败都要逆序回滚前一步，所以这几个接口必须能被程序稳定调用。
/// </remarks>
[ApiController]
[Route("internal/points")]
public sealed class InternalPointController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public InternalPointController(IMediator mediator) => _mediator = mediator;

    /// <summary>发放积分。注册赠送 / 订单完成 / 首评都走这里。</summary>
    /// <param name="command">发放命令，BizNo 是幂等键的一部分。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额；超出余额上限会被截断。</returns>
    [HttpPost("Earn")]
    public Task<ApiResponse<PointBalance>> Earn([FromBody] EarnPointsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>发放注册赠送积分。<b>金额由积分规则决定</b>，调用方不传。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额。</returns>
    /// <remarks>
    /// 与 <c>Earn</c> 分开是因为金额的归属不同：注册赠送的数额是积分规则里的一项，
    /// 让客户服务传金额等于把规则抄了一份过去 —— 运营改了规则那边不生效，且不会报错。
    /// </remarks>
    [HttpPost("EarnRegisterGift")]
    public Task<ApiResponse<PointBalance>> EarnRegisterGift(
        [FromBody] GrantRegisterGiftCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>发放发表首评赠送积分。<b>金额由积分规则决定</b>，调用方不传。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额。</returns>
    [HttpPost("EarnEvaluateGift")]
    public Task<ApiResponse<PointBalance>> EarnEvaluateGift(
        [FromBody] GrantEvaluateGiftCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>下单冻结积分。</summary>
    /// <param name="command">冻结命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额；积分不足返回业务错误。</returns>
    [HttpPost("Lock")]
    public Task<ApiResponse<PointBalance>> Lock([FromBody] LockPointsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>取消 / 超时关单解冻，退回原发放批次。</summary>
    /// <param name="command">解冻命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额。</returns>
    [HttpPost("Unfreeze")]
    public Task<ApiResponse<PointBalance>> Unfreeze([FromBody] UnfreezePointsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>支付成功实扣。</summary>
    /// <param name="command">实扣命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额。</returns>
    [HttpPost("Consume")]
    public Task<ApiResponse<PointBalance>> Consume([FromBody] ConsumePointsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>按订单发放积分（订单完成时由 OrderService 调用）。实付每满 1 元 1 积分。</summary>
    /// <param name="command">发放命令，积分数由服务端按规则算，不接受调用方传入。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额；实付不足 1 元时返回成功但不发积分。</returns>
    /// <remarks>
    /// 之所以不直接复用 <c>Earn</c>：<c>Earn</c> 收的是**调用方算好的积分数**，
    /// 而「实付每满 1 元 1 积分」这条规则必须只有一处定义。
    /// 让订单服务自己算一遍的话，改了规则就会有两个服务算出不同的积分数，且没人会发现。
    /// </remarks>
    [HttpPost("EarnByOrder")]
    public Task<ApiResponse<PointBalance>> EarnByOrder([FromBody] EarnByOrderCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>过期扣减（每天 02:00 由 ScheduledService 调用）。</summary>
    /// <param name="command">过期命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>扫描 / 过期 / 跳过批次数与扣减积分总数。</returns>
    /// <remarks>
    /// 幂等靠批次 Id 拼出的业务号 <c>EXP-{lotId}</c>，重复扫到同一批次不会重复扣。
    /// </remarks>
    [HttpPost("Expire")]
    public Task<ApiResponse<ExpireResult>> Expire([FromBody] ExpirePointsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>退款按比例回收积分（向上取整），退回原发放批次。</summary>
    /// <param name="command">回收命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额。</returns>
    [HttpPost("Refund")]
    public Task<ApiResponse<PointBalance>> Refund([FromBody] RefundPointsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
