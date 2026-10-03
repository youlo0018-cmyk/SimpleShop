using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using OrderService.Application.Features.Orders;

namespace OrderService.Api.Controllers;

/// <summary>C 端订单接口（需登录）。</summary>
/// <remarks>
/// 游客不能下单（TEST_CASES API-CART-004 的同一条规则）：订单要归属到人，
/// 游客下的单既没法收货也没法退款，只会变成一堆没人认领的脏数据。
/// </remarks>
[ApiController]
[Route("orders")]
public sealed class OrderController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public OrderController(IMediator mediator) => _mediator = mediator;

    /// <summary>下单。</summary>
    /// <param name="command">下单命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回订单号与实付金额。</returns>
    /// <remarks>
    /// <b>同一个 <c>IdempotencyKey</c> 反复提交只会得到一张订单。</b>
    /// 客户端在进结算页时取一次键（放在结算令牌里），提交时原样带上；
    /// 网络超时后重试也用同一个键，不要每次重新生成。
    /// </remarks>
    [HttpPost("Create")]
    public Task<ApiResponse<OrderCreatedDto>> Create([FromBody] CreateOrderCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>我的订单分页。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单分页结果。</returns>
    [HttpGet("List")]
    public Task<ApiResponse<PagedResult<OrderListItemDto>>> List(
        [FromQuery] QueryMyOrdersCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>订单详情。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="customerId">客户 Id，用于校验归属。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单详情，含全部订单行。</returns>
    [HttpGet("Detail")]
    public Task<ApiResponse<OrderDetailDto>> Detail(
        [FromQuery] string orderNo, [FromQuery] long customerId, CancellationToken ct)
        => _mediator.Send(new QueryOrderDetailCommand(customerId, orderNo ?? string.Empty), ct);

    /// <summary>取消订单。只有待支付能取消。</summary>
    /// <param name="command">取消命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Cancel")]
    public Task<ApiResponse> Cancel([FromBody] CancelOrderCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>确认收货。只有已发货能确认，确认后不可退款。</summary>
    /// <param name="command">确认收货命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("ConfirmReceipt")]
    public Task<ApiResponse> ConfirmReceipt([FromBody] ConfirmReceiptCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}