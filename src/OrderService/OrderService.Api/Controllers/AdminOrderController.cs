using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using OrderService.Application.Features.OrderAdmin;
using OrderService.Application.Features.Orders;

namespace OrderService.Api.Controllers;

/// <summary>后台订单接口（运营 / 商户）。</summary>
[ApiController]
[Route("admin/orders")]
public sealed class AdminOrderController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public AdminOrderController(IMediator mediator) => _mediator = mediator;

    /// <summary>订单分页。平台 Id / 商户 Id 来自租户上下文，超管可传 0 表示不限。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单分页结果。</returns>
    [HttpPost("List")]
    public Task<ApiResponse<PagedResult<AdminOrderListItemDto>>> List(
        [FromBody] QueryAdminOrdersCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>订单详情。含全部订单行与金额构成（商品总额 / 运费 / 积分抵扣 / 实付）。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单详情。</returns>
    /// <remarks>
    /// 与 C 端 <c>orders/Detail</c> 返回同一套结构（共用 <c>OrderDetailAssembler</c>），
    /// 差别只在**不做客户归属校验** —— 后台的可见范围由网关租户上下文决定。
    /// </remarks>
    [HttpPost("Detail")]
    public Task<ApiResponse<OrderDetailDto>> Detail(
        [FromBody] QueryAdminOrderDetailCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>发货。<b>不填物流信息</b>（用户需求 D3）。</summary>
    /// <param name="command">发货命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Ship")]
    public Task<ApiResponse> Ship([FromBody] ShipOrderCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>虚拟商品发货。发货即完成（20 → 50）。</summary>
    /// <param name="command">命令，备注一般放卡号 / 激活码。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("DeliverVirtual")]
    public Task<ApiResponse> DeliverVirtual([FromBody] DeliverVirtualCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>自提备货完成，返回取货码。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回取货码（RSA 密文，前端只负责显示）。</returns>
    [HttpPost("SelfPickupReady")]
    public Task<ApiResponse<PickupCodeDto>> SelfPickupReady(
        [FromBody] SelfPickupReadyCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>核销取货码。40 → 50 已完成。</summary>
    /// <param name="command">核销命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回订单号。</returns>
    /// <remarks>
    /// 加解密<b>全部在服务端完成</b>（用户明确要求），前端只把扫码枪读到的字符串原样传上来。
    /// </remarks>
    [HttpPost("VerifyPickupCode")]
    public Task<ApiResponse<PickupCodeDto>> VerifyPickupCode(
        [FromBody] VerifyPickupCodeCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>模拟支付（仅测试环境）。可选择支付成功或失败。</summary>
    /// <param name="command">命令，Succeed 决定模拟结果。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>返回模拟支付结果与订单当前状态。</returns>
    /// <remarks>
    /// 与真实支付回调走<b>同一条</b>支付收尾链路，所以这里测通了不代表线上一定通，
    /// 但至少不会出现「模拟能过、真支付走不通」这种只在收钱时才暴露的问题。
    /// </remarks>
    [HttpPost("SimulatePayment")]
    public Task<ApiResponse<SimulatePaymentDto>> SimulatePayment(
        [FromBody] SimulatePaymentCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>退款。虚拟商品订单不支持退款。</summary>
    /// <param name="command">退款命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Refund")]
    public Task<ApiResponse> Refund([FromBody] RefundOrderCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
