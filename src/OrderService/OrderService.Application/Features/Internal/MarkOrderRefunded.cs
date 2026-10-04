using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;

namespace OrderService.Application.Features.Internal;

/// <summary>把订单标记为已退款（PaymentService 审批通过后调用）。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="RefundAmount">本次退款金额。</param>
public record MarkOrderRefundedCommand(string OrderNo, decimal RefundAmount) : IRequest<ApiResponse>;

/// <summary>命令校验器注册。</summary>
public static class MarkOrderRefundedValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddMarkOrderRefundedValidators(IServiceCollection services)
        => services.AddScoped<IValidator<MarkOrderRefundedCommand>, MarkOrderRefundedValidator>();

    /// <summary>校验规则。</summary>
    private sealed class MarkOrderRefundedValidator : AbstractValidator<MarkOrderRefundedCommand>
    {
        /// <summary>构造校验器。</summary>
        public MarkOrderRefundedValidator()
        {
            RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("缺少订单号");
            RuleFor(x => x.RefundAmount).GreaterThan(0m).WithMessage("退款金额必须大于 0");
        }
    }
}

/// <summary>标记订单已退款的处理器。</summary>
public sealed class MarkOrderRefundedHandler : IRequestHandler<MarkOrderRefundedCommand, ApiResponse>
{
    private readonly IOrderStore _store;

    /// <summary>构造处理器。</summary>
    /// <param name="store">订单存储端口。</param>
    public MarkOrderRefundedHandler(IOrderStore store) => _store = store;

    /// <summary>执行标记。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(MarkOrderRefundedCommand request, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);
        if (order is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "订单不存在");

        if (order.Status == OrderStatuses.Refunded)
        {
            // 幂等：已经退过直接成功。退款审批可能被重复调用（重试、运营多点一次），
            // 报错会让上游以为失败而反复重试
            return ApiResponseFactory.Ok("订单已是已退款状态");
        }

        var affected = await _store.TryTransitStatusAsync(order.Id, order.Status, OrderStatuses.Refunded, ct).ConfigureAwait(false);
        if (affected == 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.OrderStateInvalid, "订单状态已变更，请刷新后重试");
        }

        return ApiResponseFactory.Ok("订单已标记为已退款");
    }
}
