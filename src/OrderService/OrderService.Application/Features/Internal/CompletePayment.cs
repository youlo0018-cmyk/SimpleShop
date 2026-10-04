using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Application.Features.OrderAdmin;
using OrderService.Domain.Ports;

namespace OrderService.Application.Features.Internal;

/// <summary>支付完成收尾（PaymentService 支付成功后调用）。</summary>
/// <param name="OrderNo">订单号。</param>
public record CompletePaymentCommand(string OrderNo) : IRequest<ApiResponse>;

/// <summary>命令校验器注册。</summary>
public static class CompletePaymentValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddCompletePaymentValidators(IServiceCollection services)
        => services.AddScoped<IValidator<CompletePaymentCommand>, CompletePaymentValidator>();

    /// <summary>校验规则。</summary>
    private sealed class CompletePaymentValidator : AbstractValidator<CompletePaymentCommand>
    {
        /// <summary>构造校验器。</summary>
        public CompletePaymentValidator()
            => RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("缺少订单号");
    }
}

/// <summary>支付完成收尾处理器。</summary>
/// <remarks>
/// <b>为什么复用 <c>OrderPaymentCompleter</c> 而不是自己实现一遍</b>：
/// 支付成功的副作用有四步（库存确认 / 积分冻结转实扣 / 券核销 / 订单转已支付），
/// 少做一步就是资损或超卖。在支付服务里复制一份，迟早会和这里的实现漂移。
/// 支付服务只负责「钱收到了没有」，剩下的订单侧副作用一律由订单服务自己完成。
/// </remarks>
public sealed class CompletePaymentHandler : IRequestHandler<CompletePaymentCommand, ApiResponse>
{
    private readonly IOrderStore _store;
    private readonly OrderPaymentCompleter _completer;

    /// <summary>构造处理器。</summary>
    /// <param name="store">订单存储端口。</param>
    /// <param name="completer">支付收尾服务。</param>
    public CompletePaymentHandler(IOrderStore store, OrderPaymentCompleter completer)
    {
        _store = store;
        _completer = completer;
    }

    /// <summary>执行收尾。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(CompletePaymentCommand request, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(request.OrderNo.Trim(), ct).ConfigureAwait(false);
        if (order is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "订单不存在");

        var outcome = await _completer.CompleteAsync(order, ct).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            return ApiResponseFactory.Fail(
                outcome.FailedStep == 1 ? BaseApiResponseCode.StockNotEnough : BaseApiResponseCode.BusinessError,
                outcome.Error);
        }

        return ApiResponseFactory.Ok("支付完成");
    }
}
