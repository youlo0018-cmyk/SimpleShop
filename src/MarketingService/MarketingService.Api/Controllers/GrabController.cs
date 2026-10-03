using Collaboration.Domain.Common;
using MarketingService.Application.Features.Seckill;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace MarketingService.Api.Controllers;

/// <summary>C 端秒杀抢购接口（需登录）。</summary>
/// <remarks>
/// 单独一个控制器而不是塞进 <c>ShopSeckillController</c>：那边的备注写明了
/// 「前台秒杀频道页<b>只有读接口</b>」。抢购是写动作，混在一起会让那条约束形同虚设。
/// </remarks>
[ApiController]
[Route("marketing/seckill/grab")]
public sealed class GrabController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public GrabController(IMediator mediator) => _mediator = mediator;

    /// <summary>抢购。</summary>
    /// <param name="command">抢购命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>抢购结果，含 requestId、订单号与中文提示。</returns>
    /// <remarks>
    /// <b>HTTP 一律 200</b>：抢完、限购、下单失败都是**正常业务结果**，不是接口出错。
    /// 靠 <c>resultStatus</c> 区分，前端拿 <c>requestId</c> 去轮询结果。
    /// </remarks>
    [HttpPost]
    public Task<ApiResponse<GrabResultDto>> Grab([FromBody] GrabSeckillCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>轮询抢购结果。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>抢购结果。</returns>
    /// <remarks>
    /// 当前是同步下单，接口返回时结果已经定了；保留它是为了将来接 MQ 异步下单时前端不用改。
    /// </remarks>
    [HttpPost("result")]
    public Task<ApiResponse<GrabResultDto>> Result(
        [FromBody] QueryGrabResultCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
