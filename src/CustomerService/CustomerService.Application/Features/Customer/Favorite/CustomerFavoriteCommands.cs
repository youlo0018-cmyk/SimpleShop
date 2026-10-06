using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;

namespace CustomerService.Application.Features.Customer.Favorite;

/// <summary>收藏项。</summary>
/// <param name="SpuId">商品 SPU Id。</param>
/// <param name="FavoritedAt">收藏时间（展示用，Asia/Shanghai）。</param>
/// <remarks>
/// <b>只回商品 Id</b>：商品信息在商品服务，收藏表里没有也不该有快照
/// （商品改名 / 下架之后快照就成了假的）。客户端拿 Id 去 <c>/shop/products/Detail</c> 取展示信息，
/// 单客户收藏上限 20 条，一次最多 20 次详情请求。
/// 「商品服务按 Id 批量查询」在 REVIEW.md P2-23 里跟踪。
/// </remarks>
public sealed record CustomerFavoriteDto(string SpuId, string FavoritedAt);

/// <summary>查自己的收藏。</summary>
/// <param name="CustomerId">客户 Id；经网关时必须与令牌一致。</param>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryCustomerFavoritesCommand(long CustomerId, int Page = 1, int PageSize = 20)
    : IRequest<ApiResponse<PagedResult<CustomerFavoriteDto>>>;

/// <summary>收藏商品。</summary>
/// <param name="CustomerId">客户 Id；经网关时必须与令牌一致。</param>
/// <param name="SpuId">商品 SPU Id。</param>
public record AddCustomerFavoriteCommand(long CustomerId, long SpuId)
    : IRequest<ApiResponse>;

/// <summary>取消收藏。</summary>
/// <param name="CustomerId">客户 Id；经网关时必须与令牌一致。</param>
/// <param name="SpuId">商品 SPU Id。</param>
public record RemoveCustomerFavoriteCommand(long CustomerId, long SpuId)
    : IRequest<ApiResponse>;

/// <summary>单客户收藏上限。</summary>
/// <remarks>上限是产品口径（REVIEW.md P2-23 的同一段说明）：收藏是「想买」的短名单，
/// 不设上限会变成第二个购物车，运营侧也没法拿它做任何分析。</remarks>
public static class CustomerFavoriteLimits
{
    /// <summary>最多收藏多少个商品。</summary>
    public const int MaxPerCustomer = 20;
}

/// <summary>收藏分页校验。</summary>
public sealed class QueryCustomerFavoritesValidator : AbstractValidator<QueryCustomerFavoritesCommand>
{
    /// <summary>构造校验器。</summary>
    public QueryCustomerFavoritesValidator()
    {
        RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户信息不正确");
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须大于 0");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("每页条数必须在 1 ~ 100 之间");
    }
}

/// <summary>收藏 / 取消收藏校验。</summary>
public sealed class AddCustomerFavoriteValidator : AbstractValidator<AddCustomerFavoriteCommand>
{
    /// <summary>构造校验器。</summary>
    public AddCustomerFavoriteValidator()
    {
        RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户信息不正确");
        RuleFor(x => x.SpuId).GreaterThan(0).WithMessage("商品信息不正确");
    }
}

/// <summary>取消收藏校验。</summary>
public sealed class RemoveCustomerFavoriteValidator : AbstractValidator<RemoveCustomerFavoriteCommand>
{
    /// <summary>构造校验器。</summary>
    public RemoveCustomerFavoriteValidator()
    {
        RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户信息不正确");
        RuleFor(x => x.SpuId).GreaterThan(0).WithMessage("商品信息不正确");
    }
}
