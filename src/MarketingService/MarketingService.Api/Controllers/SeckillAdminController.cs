using Collaboration.Domain.Common;
using MarketingService.Application.Features.Seckill;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace MarketingService.Api.Controllers;

/// <summary>秒杀场次后台接口。</summary>
[ApiController]
[Route("marketing/seckill/sessions")]
public sealed class SeckillAdminController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public SeckillAdminController(IMediator mediator) => _mediator = mediator;

    /// <summary>新建场次（未开始，库存未划出）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回场次 Id。</returns>
    [HttpPost("Create")]
    public Task<ApiResponse<long>> Create([FromBody] CreateSessionCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>编辑场次。<b>库存已划出后不允许改。</b></summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Update")]
    public Task<ApiResponse> Update([FromBody] UpdateSessionCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>场次分页。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>场次分页，含状态中文名与是否已划出库存。</returns>
    [HttpPost("List")]
    public Task<ApiResponse<SeckillSessionPage>> List([FromBody] QuerySessionsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>秒杀场次下拉（DATA_SPEC 4.2）：只返回未开始 / 进行中的场次。</summary>
    /// <param name="limit">最多返回多少条，1-200。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下拉项 <c>{ value, label, status, statusName }</c>。</returns>
    /// <remarks>
    /// 加场次商品时要选场次，走列表接口再自己过滤的话，
    /// 下拉里会出现已结束 / 已取消的场次 —— 选中后加商品必然失败。
    /// </remarks>
    [HttpGet("Options")]
    public Task<ApiResponse<List<SeckillSessionOption>>> Options(
        [FromQuery] int limit = 200, CancellationToken ct = default)
        => _mediator.Send(new QuerySeckillSessionOptionsCommand(limit), ct);

    /// <summary>发布场次：<b>从常规库存划出</b>并置为进行中。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>划出结果，含失败 SKU 清单。</returns>
    /// <remarks>
    /// 重复点发布会被拒绝（除非显式 <c>Force</c>）——重复划转会从常规库存里扣走第二份，
    /// 而秒杀池子里只有一份货，等于凭空蒸发一批库存。
    /// </remarks>
    [HttpPost("Publish")]
    public Task<ApiResponse<SeckillPublishResult>> Publish([FromBody] PublishSessionCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>结束 / 中止场次：<b>剩余库存立即回补</b>。</summary>
    /// <param name="command">命令，Cancel = true 为手动中止。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回补结果，含失败 SKU 清单。</returns>
    [HttpPost("Finish")]
    public Task<ApiResponse<SeckillFinishResult>> Finish([FromBody] FinishSessionCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>场次内添加商品（只能发布前添加）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回商品 Id。</returns>
    /// <remarks>秒杀价必须低于商品原价，否则「秒杀」比原价还贵。</remarks>
    [HttpPost("Items/Add")]
    public Task<ApiResponse<long>> AddItem([FromBody] AddSessionItemCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>场次商品列表（后台）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>商品列表，含已抢与剩余。</returns>
    [HttpPost("Items/List")]
    public Task<ApiResponse<List<SessionItemDto>>> ListItems(
        [FromBody] QuerySessionItemsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>删除场次商品（只能发布前删除）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Items/Delete")]
    public Task<ApiResponse> DeleteItem([FromBody] DeleteSessionItemCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
