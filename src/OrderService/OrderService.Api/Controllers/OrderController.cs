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

    /// <summary>结算试算：返回服务端算出的金额拆分与可用券。</summary>
    /// <param name="command">试算命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>金额拆分、商品行与可用券。</returns>
    /// <remarks>
    /// <para><b>全程只读</b>：不占券、不锁积分、不锁库存、不落库。
    /// 游客（customerId = 0）也能试算，只是没有券与积分可算。</para>
    ///
    /// <para>金额一律以本接口为准。小程序此前在前端自己算「商品金额 − 券优惠」，
    /// 既不含运费也不含活动与积分，于是页面显示的「预计应付」与真实下单金额对不上，
    /// 而界面上没有任何地方解释差额。金额必须由服务端出，前端只负责显示。</para>
    ///
    /// <para>试算与下单共用 <c>OrderPricingResolver</c>，所以两边的单价、运费、
    /// 积分上限必然一致；改动只落在那一处，不会出现「改了这边忘了那边」。</para>
    /// </remarks>
    [HttpPost("Preview")]
    public Task<ApiResponse<PreviewOrderResult>> Preview(
        [FromBody] PreviewOrderCommand command, CancellationToken ct)
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
