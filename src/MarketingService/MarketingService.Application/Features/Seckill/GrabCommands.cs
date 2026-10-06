using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using MarketingService.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace MarketingService.Application.Features.Seckill;

/// <summary>抢购。</summary>
/// <param name="ItemId">秒杀商品 Id。</param>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="ReceiverName">收货人。</param>
/// <param name="ReceiverPhone">收货电话。</param>
/// <param name="ReceiverAddress">收货地址。</param>
/// <param name="CouponId">使用的券 Id，0 表示不用。</param>
/// <param name="PointsToUse">抵扣积分数，0 表示不用。</param>
public record GrabSeckillCommand(
    long ItemId,
    long CustomerId,
    string ReceiverName = "",
    string ReceiverPhone = "",
    string ReceiverAddress = "",
    long CouponId = 0,
    long PointsToUse = 0) : IRequest<ApiResponse<GrabResultDto>>;

/// <summary>轮询抢购结果。</summary>
/// <param name="RequestId">请求 Id。</param>
/// <param name="CustomerId">客户 Id，用于校验归属。</param>
public record QueryGrabResultCommand(string RequestId, long CustomerId)
    : IRequest<ApiResponse<GrabResultDto>>;

/// <summary>抢购结果。</summary>
/// <param name="RequestId">请求 Id。</param>
/// <param name="ResultStatus">结果码，见 <see cref="SeckillGrabResults"/>。</param>
/// <param name="Message">结果说明（中文，面向用户）。</param>
/// <param name="OrderId">订单 Id，0 表示没下单。</param>
/// <param name="OrderNo">订单号。</param>
public sealed record GrabResultDto(
    string RequestId, int ResultStatus, string Message, long OrderId, string OrderNo);

/// <summary>抢购命令的校验器注册。</summary>
public static class GrabValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddGrabValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<GrabSeckillCommand>, GrabSeckillValidator>();
        services.AddScoped<IValidator<QueryGrabResultCommand>, QueryGrabResultValidator>();
        services.AddScoped<IValidator<ReleaseSeckillGrabCommand>, ReleaseSeckillGrabValidator>();
    }

    /// <summary>抢购校验。</summary>
    private sealed class GrabSeckillValidator : AbstractValidator<GrabSeckillCommand>
    {
        /// <summary>构造校验器。</summary>
        public GrabSeckillValidator()
        {
            // 游客不能抢：限购额度要挂在人身上，没有身份就限制不住一个人买走整场
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("请先登录");
            RuleFor(x => x.ItemId).GreaterThan(0).WithMessage("商品 Id 必须为正数");
        }
    }

    /// <summary>轮询结果校验。</summary>
    private sealed class QueryGrabResultValidator : AbstractValidator<QueryGrabResultCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryGrabResultValidator()
        {
            RuleFor(x => x.RequestId).NotEmpty().MaximumLength(64).WithMessage("请求 Id 不正确");
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("请先登录");
        }
    }
}
