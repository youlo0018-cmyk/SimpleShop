using Collaboration.Domain.Common;
using MarketingService.Application.Features.Seckill;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace MarketingService.Api.Controllers;

/// <summary>营销内部接口，供定时任务调用。网关不路由 /internal 前缀。</summary>
/// <remarks>
/// <b>/internal 的隔离靠「网关不转发这个前缀」</b>（见 ocelot.json），
/// 而不是靠鉴权——定时任务拿不到后台令牌。所以端口被绕过网关直接访问时，
/// 这些接口是能被调到的，这一点与 InventoryService / OrderService 的 /internal 一致。
/// </remarks>
[ApiController]
[Route("internal/marketing")]
public sealed class InternalMarketingController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public InternalMarketingController(IMediator mediator) => _mediator = mediator;

    /// <summary>结束所有到期的秒杀场次并回补库存（定时任务调用）。</summary>
    /// <param name="command">命令，Limit 为单轮上限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>扫描 / 结束 / 回补统计。</returns>
    /// <remarks>
    /// 没有这个接口，一个没人手动中止的场次会永远停在「进行中」，
    /// 剩余库存永久锁在秒杀池里——而且没有任何报错，
    /// 现象只是商品「一直缺货」，排查时完全看不出是这个原因。
    /// </remarks>
    [HttpPost("seckill/sessions/FinishExpired")]
    public Task<ApiResponse<FinishExpiredResult>> FinishExpiredSessions(
        [FromBody] FinishExpiredSessionsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>秒杀单退款：把货退回秒杀池（订单服务调用）。</summary>
    /// <param name="command">回退命令，含客户、SKU、件数与订单号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际回退件数。</returns>
    /// <remarks>
    /// 秒杀库存是**发布场次时从常规池划走**的，秒杀单从头到尾没锁过常规库存。
    /// 所以退款绝不能走常规库存的 release —— 那笔锁定根本不存在，必然失败；
    /// 正确做法是把 <c>sold_count</c> 减回去，货由场次结束时的
    /// 「<c>seckill_stock − sold_count</c>」自然回到常规池。
    /// </remarks>
    [HttpPost("seckill/grabs/Release")]
    public Task<ApiResponse<ReleaseGrabResult>> ReleaseGrab(
        [FromBody] ReleaseSeckillGrabCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
