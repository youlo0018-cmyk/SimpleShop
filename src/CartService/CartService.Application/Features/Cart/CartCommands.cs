using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CartService.Application.Features.Cart;

/// <summary>加购（<b>累加</b>语义）。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Delta">数量增量：加购传购买数量，购物车加减传 ±1。<b>不是目标数量。</b></param>
public record AddToCartCommand(long CustomerId, long SkuId, int Delta = 1)
    : IRequest<ApiResponse<CartItemDto>>;

/// <summary>直接设置数量（不是累加）。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="CartId">购物车行 Id。</param>
/// <param name="Quantity">目标数量，0 表示删除该行。</param>
public record SetCartQuantityCommand(long CustomerId, long CartId, int Quantity)
    : IRequest<ApiResponse>;

/// <summary>删除一行。</summary>
public record RemoveCartItemCommand(long CustomerId, long CartId) : IRequest<ApiResponse>;

/// <summary>设置勾选状态。</summary>
public record SetCartCheckedCommand(long CustomerId, long CartId, bool Checked) : IRequest<ApiResponse>;

/// <summary>清空购物车。</summary>
public record ClearCartCommand(long CustomerId) : IRequest<ApiResponse>;

/// <summary>查询购物车。</summary>
public record QueryCartCommand(long CustomerId) : IRequest<ApiResponse<List<CartItemDto>>>;

/// <summary>购物车行。</summary>
public record CartItemDto(
    string Id, long SkuId, long ProductId, string SkuName, string SkuSpecText,
    decimal Price, decimal OriginalPrice, string Image, int Quantity, decimal SubTotal, bool Checked);

/// <summary>购物车命令的校验器注册。</summary>
public static class CartValidators
{
    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddCartValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<AddToCartCommand>, AddToCartValidator>();
        services.AddScoped<IValidator<SetCartQuantityCommand>, SetCartQuantityValidator>();
    }

    /// <summary>加购校验。</summary>
    /// <remarks>
    /// 这里**不**校验「结果数量 ≤ 99」——那是累加之后才知道的事，得在处理器里算。
    /// 校验器只管入参形状。
    /// </remarks>
    private sealed class AddToCartValidator : AbstractValidator<AddToCartCommand>
    {
        /// <summary>构造校验器。</summary>
        public AddToCartValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("请先登录");
            RuleFor(x => x.SkuId).GreaterThan(0).WithMessage("请选择商品规格");
            RuleFor(x => x.Delta).InclusiveBetween(-99, 99).WithMessage("单次数量变化必须在 -99 ~ 99 之间");
        }
    }

    /// <summary>设置数量校验。</summary>
    private sealed class SetCartQuantityValidator : AbstractValidator<SetCartQuantityCommand>
    {
        /// <summary>构造校验器。</summary>
        public SetCartQuantityValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("请先登录");
            RuleFor(x => x.CartId).GreaterThan(0).WithMessage("购物车行 Id 必须为正数");
            RuleFor(x => x.Quantity).InclusiveBetween(0, Domain.Entities.CartRules.MaxQuantity)
                .WithMessage($"数量必须在 0 ~ {Domain.Entities.CartRules.MaxQuantity} 之间");
        }
    }
}