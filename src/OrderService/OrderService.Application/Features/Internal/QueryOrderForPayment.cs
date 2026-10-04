using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;

namespace OrderService.Application.Features.Internal;

/// <summary>按订单号取支付 / 退款所需的订单信息。</summary>
/// <param name="OrderNo">订单号。</param>
public record QueryOrderForPaymentCommand(string OrderNo)
    : IRequest<ApiResponse<OrderForPaymentDto>>;

/// <summary>命令校验器注册。</summary>
public static class QueryOrderForPaymentValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddQueryOrderForPaymentValidators(IServiceCollection services)
        => services.AddScoped<IValidator<QueryOrderForPaymentCommand>, QueryOrderForPaymentValidator>();

    /// <summary>校验规则。</summary>
    private sealed class QueryOrderForPaymentValidator : AbstractValidator<QueryOrderForPaymentCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryOrderForPaymentValidator()
            => RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("缺少订单号");
    }
}

/// <summary>支付 / 退款订单信息查询处理器。</summary>
/// <remarks>
/// <b>金额一律服务端反查</b>（规格 10.1）：支付服务不接收客户端传入的金额。
/// 客户端报多少不是关键，「这笔单子实际该付多少」才关键——
/// 金额由服务端算出来，才不会出现「改一下前端就能少付」。
/// </remarks>
public sealed class QueryOrderForPaymentHandler
    : IRequestHandler<QueryOrderForPaymentCommand, ApiResponse<OrderForPaymentDto>>
{
    private readonly IOrderStore _store;

    /// <summary>构造处理器。</summary>
    /// <param name="store">订单存储端口。</param>
    public QueryOrderForPaymentHandler(IOrderStore store) => _store = store;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单信息；不存在返回 404。</returns>
    public async Task<ApiResponse<OrderForPaymentDto>> Handle(
        QueryOrderForPaymentCommand request, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);
        if (order is null)
        {
            return ApiResults.Fail<OrderForPaymentDto>(BaseApiResponseCode.NotFound, "订单不存在");
        }

        var items = await _store.ListItemsAsync(order.Id, ct).ConfigureAwait(false);

        var dto = new OrderForPaymentDto(
            order.Id, order.OrderNo, order.CustomerId,
            order.PlatformId, order.MerchantId,
            order.Status, OrderStatusMachine.NameOf(order.Status),
            order.GoodsTotal, order.Freight, order.PointsDeduction, order.PayableAmount,
            order.PointsUsed, order.CouponId,
            items.Select(a => new OrderItemForPaymentDto(
                a.Id, a.SkuId, a.ProductName, a.SkuSpecText,
                a.Quantity, a.DeliveryType, a.PayableAmount)).ToList());

        return ApiResults.Ok(dto);
    }
}
