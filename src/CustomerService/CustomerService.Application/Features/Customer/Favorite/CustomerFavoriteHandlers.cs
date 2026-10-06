using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using CustomerService.Domain.Entities;
using CustomerService.Domain.IRepository;
using MediatR;

namespace CustomerService.Application.Features.Customer.Favorite;

/// <summary>查自己的收藏。</summary>
public sealed class QueryCustomerFavoritesHandler
    : IRequestHandler<QueryCustomerFavoritesCommand, ApiResponse<PagedResult<CustomerFavoriteDto>>>
{
    private readonly ICustomerFavoriteRepository _favorites;

    /// <summary>构造处理器。</summary>
    /// <param name="favorites">收藏仓储。</param>
    public QueryCustomerFavoritesHandler(ICustomerFavoriteRepository favorites) => _favorites = favorites;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>收藏分页，按收藏时间倒序。</returns>
    public async Task<ApiResponse<PagedResult<CustomerFavoriteDto>>> Handle(
        QueryCustomerFavoritesCommand request, CancellationToken ct)
    {
        CustomerScope.Require(request.CustomerId);

        var (items, total) = await _favorites
            .QueryPagedAsync(request.Page, request.PageSize, ct).ConfigureAwait(false);

        var dtos = items.Select(a => new CustomerFavoriteDto(
            a.SpuId.ToString(),
            // 与其余 C 端列表同一口径：库里存 UTC，**接口也回 UTC**，转 Asia/Shanghai 由前端做
            // （DATA_SPEC 4.8；服务端 ToLocalTime 会把正确性绑在容器时区上）
            DateTime.SpecifyKind(a.FavoritedAt, DateTimeKind.Utc)
                .ToString("yyyy-MM-dd HH:mm:ss"))).ToList();

        return ApiResults.Ok(new PagedResult<CustomerFavoriteDto>(
            dtos, total, request.Page, request.PageSize));
    }
}

/// <summary>收藏商品。</summary>
public sealed class AddCustomerFavoriteHandler
    : IRequestHandler<AddCustomerFavoriteCommand, ApiResponse>
{
    private readonly ICustomerFavoriteRepository _favorites;

    /// <summary>构造处理器。</summary>
    /// <param name="favorites">收藏仓储。</param>
    public AddCustomerFavoriteHandler(ICustomerFavoriteRepository favorites) => _favorites = favorites;

    /// <summary>执行收藏。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// <b>重复收藏按幂等成功返回</b>：用户连点两下「收藏」不该看到报错，
    /// 而且小程序可能在弱网下重试。真正要拦的是「超出上限」。
    ///
    /// <para><b>不校验商品是否存在</b>：收藏是「书签」，商品服务抖动时不该让收藏失败；
    /// 而且这里只存 Id，商品下架 / 删除后收藏记录仍然有效（列表页按 Id 取详情时自然会少一条）。
    /// 真正必须校验商品的地方是下单，那里以商品服务为准。</para>
    /// </remarks>
    public async Task<ApiResponse> Handle(AddCustomerFavoriteCommand request, CancellationToken ct)
    {
        CustomerScope.Require(request.CustomerId);

        if (await _favorites.ExistsAsync(request.SpuId, ct).ConfigureAwait(false))
        {
            return ApiResponseFactory.Ok("已在收藏夹里");
        }

        var count = await _favorites.CountAsync(ct).ConfigureAwait(false);
        if (count >= CustomerFavoriteLimits.MaxPerCustomer)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.QuotaNotEnough,
                $"收藏夹最多 {CustomerFavoriteLimits.MaxPerCustomer} 件，请先取消一些");
        }

        await _favorites.InsertAsync(new CustomerFavorite
        {
            SpuId = request.SpuId,
            FavoritedAt = DateTime.UtcNow
        }, ct).ConfigureAwait(false);

        return ApiResponseFactory.Ok("已收藏");
    }
}

/// <summary>取消收藏。</summary>
public sealed class RemoveCustomerFavoriteHandler
    : IRequestHandler<RemoveCustomerFavoriteCommand, ApiResponse>
{
    private readonly ICustomerFavoriteRepository _favorites;

    /// <summary>构造处理器。</summary>
    /// <param name="favorites">收藏仓储。</param>
    public RemoveCustomerFavoriteHandler(ICustomerFavoriteRepository favorites) => _favorites = favorites;

    /// <summary>执行取消。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>没收藏过也返回成功（幂等）：用户的目标是「不在收藏夹里」，那个状态已经达成。</remarks>
    public async Task<ApiResponse> Handle(RemoveCustomerFavoriteCommand request, CancellationToken ct)
    {
        CustomerScope.Require(request.CustomerId);

        await _favorites.DeleteBySpuAsync(request.SpuId, ct).ConfigureAwait(false);
        return ApiResponseFactory.Ok("已取消收藏");
    }
}
