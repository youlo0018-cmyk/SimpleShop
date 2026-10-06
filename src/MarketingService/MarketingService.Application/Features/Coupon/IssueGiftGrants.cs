using Collaboration.Domain.Common;
using FluentValidation;
using MarketingService.Domain.IRepository;
using MediatR;

namespace MarketingService.Application.Features.Coupon;

/// <summary>发放某订单的满赠券（支付成功时由订单服务调用）。</summary>
/// <param name="OrderNo">订单号。</param>
/// <remarks>
/// 下单试算时已把「这单该送什么」写成发放承诺（<c>gift_grant</c>），
/// 这里只负责按承诺发券。接口在 <c>/internal/marketing</c> 下：它是订单服务调用的内部能力，
/// 不是给小程序或后台用的。
/// </remarks>
public record IssueGiftGrantsCommand(string OrderNo) : IRequest<ApiResponse<GiftIssueResult>>;

/// <summary>满赠发券结果。</summary>
/// <param name="Promised">本单承诺的发放条数。</param>
/// <param name="Issued">本次真正发出去的条数。</param>
/// <param name="CouponCount">本次真正发出去的券张数。</param>
/// <param name="Failed">发不出去的条数（赠送模板已被删除），这些留在待发放供人工核对。</param>
public sealed record GiftIssueResult(int Promised, int Issued, int CouponCount, int Failed);

/// <summary>满赠发券处理器。</summary>
public sealed class IssueGiftGrantsHandler
    : IRequestHandler<IssueGiftGrantsCommand, ApiResponse<GiftIssueResult>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public IssueGiftGrantsHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <summary>执行发放。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>发放统计。</returns>
    public async Task<ApiResponse<GiftIssueResult>> Handle(
        IssueGiftGrantsCommand request, CancellationToken ct)
    {
        var outcome = await _coupons
            .IssueGiftGrantsAsync(request.OrderNo, DateTime.UtcNow, ct)
            .ConfigureAwait(false);

        return ApiResults.Ok(
            new GiftIssueResult(outcome.Promised, outcome.Issued, outcome.CouponCount, outcome.Failed),
            outcome.CouponCount > 0 ? $"已发放 {outcome.CouponCount} 张满赠券" : "本单没有待发放的满赠券");
    }
}

/// <summary>满赠发券校验。</summary>
public sealed class IssueGiftGrantsValidator : AbstractValidator<IssueGiftGrantsCommand>
{
    /// <summary>构造校验器。</summary>
    public IssueGiftGrantsValidator()
    {
        RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64)
            .WithMessage("订单号必填且不超过 64 个字符");
    }
}
