using CartService.Application.Features.Cart;
using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CartService.Api.Controllers;

/// <summary>购物车（C 端，需登录）。</summary>
/// <remarks>
/// 游客加购一律 401（TEST_CASES API-CART-004）：购物车要持久化到数据库，
/// 而游客没有可归属的身份，存下来也没人认领。
/// </remarks>
[ApiController]
[Route("carts")]
public sealed class CartController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public CartController(IMediator mediator) => _mediator = mediator;

    /// <summary>加购（<b>累加</b>：传的是增量，不是目标数量）。</summary>
    /// <param name="command">加购命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回该购物车行。</returns>
    /// <remarks>
    /// 连续加购 1、1、1 得到 3 件，不是 6 件 —— 这是 TEST_CASES API-CART-001 的 P0 用例。
    /// </remarks>
    [HttpPost("Add")]
    public Task<ApiResponse<CartItemDto>> Add([FromBody] AddToCartCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>查询购物车。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>购物车行列表，含小计。</returns>
    [HttpGet("List")]
    public Task<ApiResponse<List<CartItemDto>>> List([FromQuery] long customerId, CancellationToken ct)
        => _mediator.Send(new QueryCartCommand(customerId), ct);

    /// <summary>直接设置数量（不是累加）。传 0 表示删除该行。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("SetQuantity")]
    public Task<ApiResponse> SetQuantity([FromBody] SetCartQuantityCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>删除一行。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Remove")]
    public Task<ApiResponse> Remove([FromBody] RemoveCartItemCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>设置勾选状态。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("SetChecked")]
    public Task<ApiResponse> SetChecked([FromBody] SetCartCheckedCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>清空购物车。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Clear")]
    public Task<ApiResponse> Clear([FromBody] ClearCartCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}