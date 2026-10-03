using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain.Entities;

namespace OrderService.Application.Features.Orders;

/// <summary>
/// 秒杀下单。
/// </summary>
/// <remarks>
/// <para><b>与普通下单的唯一区别：库存已经在下单前被预扣走了。</b>
/// 秒杀的货在**发布场次时**就从常规库存划走，所以这里必须跳过「锁常规库存」，
/// 否则等于锁走第二份，秒杀直接超卖。</para>
///
/// <para>因此<b>幂等责任落到调用方</b>：既然这步不校验库存，调用方必须保证
/// 「扣库存」与「下单」之间不会重复扣。MarketingService 侧靠
/// 限购幂等键 <c>{itemId}:{customerId}</c> 的唯一索引保证。</para>
/// </remarks>
public record CreateSeckillOrderCommand(
    long CustomerId,
    long PlatformId,
    long MerchantId,
    string IdempotencyKey,
    string ReceiverName,
    string ReceiverPhone,
    string ReceiverAddress,
    long SpuId,
    long SkuId,
    int Quantity,
    decimal SeckillPrice,
    string ProductName,
    string SkuSpecText,
    int DeliveryType = 1,
    long CouponId = 0,
    long PointsToUse = 0,
    decimal Freight = 0m,
    string Remark = "") : IRequest<ApiResponse<OrderCreatedDto>>;

/// <summary>秒杀下单命令的校验器注册。</summary>
public static class SeckillOrderValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddSeckillOrderValidators(IServiceCollection services)
        => services.AddScoped<IValidator<CreateSeckillOrderCommand>, CreateSeckillOrderValidator>();

    /// <summary>秒杀下单校验。</summary>
    private sealed class CreateSeckillOrderValidator : AbstractValidator<CreateSeckillOrderCommand>
    {
        /// <summary>构造校验器。</summary>
        public CreateSeckillOrderValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("请先登录");
            RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(64)
                .WithMessage("缺少幂等键");
            // 收货信息只对**实物**（快递 / 自提）必填。
            // 虚拟商品没有物流也没有收货人，硬要求会让虚拟秒杀 100% 下单失败，
            // 而用户在小程序上根本没有填地址的机会（抢购是一键动作）。
            When(x => x.DeliveryType != DeliveryTypes.Virtual, () =>
            {
                RuleFor(x => x.ReceiverName).NotEmpty().MaximumLength(64).WithMessage("请填写收货人姓名");
                RuleFor(x => x.ReceiverPhone).NotEmpty().MaximumLength(20).WithMessage("请填写收货电话");
                RuleFor(x => x.ReceiverAddress).NotEmpty().MaximumLength(256).WithMessage("请填写收货地址");
            });

            // 数量上限压到 1：每人每场次限购 1 件，超出应在营销侧被拦掉，
            // 这里再兜一道底，防止调用方漏判导致一个人买走整场
            RuleFor(x => x.Quantity).InclusiveBetween(1, 1)
                .WithMessage("每人每场次限购 1 件");

            RuleFor(x => x.SeckillPrice).InclusiveBetween(0.01m, 9_999_999.99m)
                .WithMessage("秒杀价不正确");
            RuleFor(x => x.SkuId).GreaterThan(0).WithMessage("SKU Id 必须为正数");
            RuleFor(x => x.Freight).InclusiveBetween(0m, 9_999.99m).WithMessage("运费超出允许范围");
            RuleFor(x => x.DeliveryType).InclusiveBetween(1, 3).WithMessage("配送方式不正确");
        }
    }
}
