using Collaboration.Domain.Common;
using MarketingService.Application.Features.Seckill;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace MarketingService.Api.Controllers;

/// <summary>前台秒杀只读接口（小程序「秒杀」频道页，无需登录）。</summary>
/// <remarks>
/// 与后台的 <c>marketing/seckill/sessions</c> 分开：后台要看已结束 / 已取消的场次做复盘，
/// 前台只看「即将开场」与「进行中」。而且前台<b>只有读接口</b>——发布、中止这些动作
/// 一旦出现在这个前缀下，就等于把库存划转暴露给了匿名用户。
/// </remarks>
[ApiController]
[Route("marketing/seckill/sessions")]
public sealed class ShopSeckillController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public ShopSeckillController(IMediator mediator) => _mediator = mediator;

    /// <summary>当前可抢购的场次与商品。</summary>
    /// <param name="command">命令；SessionId 传 0 表示取全部进行中的场次。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>场次列表，含倒计时秒数与秒杀商品。</returns>
    /// <remarks>
    /// 倒计时秒数由<b>服务端</b>算好给前端，而不是把时间戳丢过去让小程序自己减：
    /// 小程序设备的本地时间不准，用户改一下系统时间，倒计时就会变成负数或卡住不动。
    /// </remarks>
    [HttpPost("Public")]
    public Task<ApiResponse<List<PublicSessionDto>>> Public(
        [FromBody] QueryPublicSessionsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}