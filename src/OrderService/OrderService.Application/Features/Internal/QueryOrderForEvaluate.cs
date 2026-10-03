using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Application.Features.Orders;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;

namespace OrderService.Application.Features.Internal;

/// <summary>按订单号取「评价所需的订单信息」（供 EvaluateService 调用）。</summary>
/// <param name="OrderNo">订单号。</param>
public record QueryOrderForEvaluateCommand(string OrderNo) : IRequest<ApiResponse<OrderForEvaluateDto>>;

/// <summary>评价用订单查询的校验器注册。</summary>
public static class QueryOrderForEvaluateValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddQueryOrderForEvaluateValidators(IServiceCollection services)
        => services.AddScoped<IValidator<QueryOrderForEvaluateCommand>, QueryOrderForEvaluateValidator>();

    /// <summary>订单号校验。</summary>
    private sealed class QueryOrderForEvaluateValidator : AbstractValidator<QueryOrderForEvaluateCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryOrderForEvaluateValidator()
        {
            RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("缺少订单号");
        }
    }
}

/// <summary>按订单号取评价所需订单信息的处理器。</summary>
/// <remarks>
/// <b>为什么订单号足够、不需要额外传 customerId</b>：调用方是服务间调用（/internal 前缀，
/// 网关不路由），订单号本身是高熵的业务主键，不存在「用户能枚举」的问题。
/// 归属校验由 EvaluateService 用自己从 JWT 拿到的 CustomerId 对比 DTO 里的 CustomerId 完成——
/// 这样「订单是不是你的」这条判断只有一个地方做（评价服务），不会两边各做一次而出现口径不一致。
/// </remarks>
public sealed class QueryOrderForEvaluateHandler
    : IRequestHandler<QueryOrderForEvaluateCommand, ApiResponse<OrderForEvaluateDto>>
{
    private readonly IOrderStore _store;

    /// <summary>构造处理器。</summary>
    /// <param name="store">订单存储端口。</param>
    public QueryOrderForEvaluateHandler(IOrderStore store) => _store = store;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单信息；订单不存在返回 404。</returns>
    public async Task<ApiResponse<OrderForEvaluateDto>> Handle(
        QueryOrderForEvaluateCommand request, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);
        if (order is null)
        {
            return ApiResults.Fail<OrderForEvaluateDto>(BaseApiResponseCode.NotFound, "订单不存在");
        }

        var items = await _store.ListItemsAsync(order.Id, ct).ConfigureAwait(false);

        // 按 SPU 聚合：一条首评是 SPU 级的，买了同一 SPU 的多个规格也只评一次（规格 14.1）
        var grouped = items
            .GroupBy(a => a.SpuId)
            .Select(g => new OrderSpuForEvaluateDto(
                g.Key,
                g.Select(a => a.ProductName).FirstOrDefault() ?? string.Empty,
                g.Select(a => new OrderSkuForEvaluateDto(a.SkuId, a.SkuSpecText, a.Id)).ToList()))
            .ToList();

        var dto = new OrderForEvaluateDto(
            order.Id, order.OrderNo, order.CustomerId, order.PlatformId, order.MerchantId,
            order.Status, OrderStatusMachine.NameOf(order.Status),
            // 「能不能评价」由订单服务判定（它拥有状态机），评价服务不复制状态常量：
            // 两边各写一份 `== 50`，订单状态机加一个中间态时就会静默失配
            CanEvaluate: order.Status == OrderStatuses.Completed,
            grouped);

        return ApiResults.Ok(dto);
    }
}
