using CartService.Application.Services;
using CartService.Domain.Entities;
using CartService.Domain.IRepository;
using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using MediatR;

namespace CartService.Application.Features.Cart;

/// <summary>加购（累加）处理器。</summary>
public sealed class AddToCartHandler : IRequestHandler<AddToCartCommand, ApiResponse<CartItemDto>>
{
    private readonly ICartRepository _carts;
    private readonly IProductClient _products;

    /// <summary>构造处理器。</summary>
    /// <param name="carts">购物车仓储。</param>
    /// <param name="products">商品服务客户端。</param>
    public AddToCartHandler(ICartRepository carts, IProductClient products)
    {
        _carts = carts;
        _products = products;
    }

    /// <summary>执行加购。</summary>
    /// <param name="request">加购命令，Delta 是<b>增量</b>。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回该购物车行。</returns>
    /// <remarks>
    /// 这里最要紧的一条：<b>累加，不是赋值</b>。
    /// 调用方传 1、1、1 结果应该是 3；如果写成 quantity = Delta，第二次就变成 1 了。
    /// 购物车页面的「+1 / −1」也是走这里，传 Delta = ±1。
    /// </remarks>
    public async Task<ApiResponse<CartItemDto>> Handle(AddToCartCommand request, CancellationToken ct)
    {
        var customerId = CustomerScope.Require(request.CustomerId);
        var snapshot = (await _products.GetSkuSnapshotsAsync([request.SkuId], ct))
            .FirstOrDefault(a => a.SkuId == request.SkuId);

        if (snapshot is null)
        {
            return ApiResults.Fail<CartItemDto>(BaseApiResponseCode.BadRequest, "该商品规格不存在或已下架");
        }

        var existing = await _carts.GetBySkuAsync(customerId, request.SkuId, ct);
        var target = (existing?.Quantity ?? 0) + request.Delta;

        // 减到 0 或以下就把这行删掉：购物车里不该存在数量 0 的行，
        // 否则结算页会渲染出一个 0 元且点得进结算的条目。
        if (target <= 0)
        {
            if (existing is not null) await _carts.SoftDeleteAsync(customerId, existing.Id, ct);
            return ApiResults.Ok(new CartItemDto(
                existing?.Id.ToString() ?? string.Empty, request.SkuId, snapshot.ProductId,
                snapshot.SkuName, snapshot.SkuSpecText, snapshot.Price, snapshot.OriginalPrice,
                snapshot.Image, 0, 0m, false), "已从购物车移除");
        }

        if (target > CartRules.MaxQuantity)
        {
            return ApiResults.Fail<CartItemDto>(
                BaseApiResponseCode.BadRequest,
                $"单个商品规格最多购买 {CartRules.MaxQuantity} 件");
        }

        // 快照每次加购都刷新：商品改名 / 改价 / 换图后，购物车要立刻跟上
        if (existing is null)
        {
            var fresh = new CartItem
            {
                CustomerId = customerId,
                SkuId = request.SkuId,
                ProductId = snapshot.ProductId,
                Quantity = target,
                SkuName = snapshot.SkuName,
                SkuSpecText = snapshot.SkuSpecText,
                Price = snapshot.Price,
                OriginalPrice = snapshot.OriginalPrice,
                Image = snapshot.Image,
                Checked = true
            };

            var id = await _carts.InsertAsync(fresh, ct);
            fresh.Id = id;   // InsertAsync 会把雪花 Id 填回实体
            return ApiResults.Ok(ToDto(fresh), "加购成功");
        }

        existing.Quantity = target;
        existing.ProductId = snapshot.ProductId;
        existing.SkuName = snapshot.SkuName;
        existing.SkuSpecText = snapshot.SkuSpecText;
        existing.Price = snapshot.Price;
        existing.OriginalPrice = snapshot.OriginalPrice;
        existing.Image = snapshot.Image;

        await _carts.UpdateAsync(existing, ct);
        return ApiResults.Ok(ToDto(existing), "加购成功");
    }

    private static CartItemDto ToDto(CartItem a) => new(
        a.Id.ToString(), a.SkuId, a.ProductId, a.SkuName, a.SkuSpecText,
        a.Price, a.OriginalPrice, a.Image, a.Quantity,
        Math.Round(a.Price * a.Quantity, 2, MidpointRounding.AwayFromZero), a.Checked);
}

/// <summary>设置数量处理器（不是累加）。</summary>
public sealed class SetCartQuantityHandler : IRequestHandler<SetCartQuantityCommand, ApiResponse>
{
    private readonly ICartRepository _carts;

    /// <summary>构造处理器。</summary>
    /// <param name="carts">购物车仓储。</param>
    public SetCartQuantityHandler(ICartRepository carts) => _carts = carts;

    /// <summary>执行设置。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(SetCartQuantityCommand request, CancellationToken ct)
    {
        var customerId = CustomerScope.Require(request.CustomerId);
        var item = await _carts.GetAsync(customerId, request.CartId, ct);
        if (item is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "购物车行不存在");

        if (request.Quantity <= 0)
        {
            await _carts.SoftDeleteAsync(customerId, request.CartId, ct);
            return ApiResponseFactory.Ok();
        }

        item.Quantity = request.Quantity;
        await _carts.UpdateAsync(item, ct);
        return ApiResponseFactory.Ok();
    }
}

/// <summary>删除购物车行处理器。</summary>
public sealed class RemoveCartItemHandler : IRequestHandler<RemoveCartItemCommand, ApiResponse>
{
    private readonly ICartRepository _carts;

    /// <summary>构造处理器。</summary>
    /// <param name="carts">购物车仓储。</param>
    public RemoveCartItemHandler(ICartRepository carts) => _carts = carts;

    /// <summary>执行删除。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(RemoveCartItemCommand request, CancellationToken ct)
    {
        var customerId = CustomerScope.Require(request.CustomerId);
        var item = await _carts.GetAsync(customerId, request.CartId, ct);
        if (item is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "购物车行不存在");

        await _carts.SoftDeleteAsync(customerId, request.CartId, ct);
        return ApiResponseFactory.Ok();
    }
}

/// <summary>设置勾选状态处理器。</summary>
public sealed class SetCartCheckedHandler : IRequestHandler<SetCartCheckedCommand, ApiResponse>
{
    private readonly ICartRepository _carts;

    /// <summary>构造处理器。</summary>
    /// <param name="carts">购物车仓储。</param>
    public SetCartCheckedHandler(ICartRepository carts) => _carts = carts;

    /// <summary>执行设置。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(SetCartCheckedCommand request, CancellationToken ct)
    {
        var customerId = CustomerScope.Require(request.CustomerId);
        var item = await _carts.GetAsync(customerId, request.CartId, ct);
        if (item is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "购物车行不存在");

        item.Checked = request.Checked;
        await _carts.UpdateAsync(item, ct);
        return ApiResponseFactory.Ok();
    }
}

/// <summary>清空购物车处理器。</summary>
public sealed class ClearCartHandler : IRequestHandler<ClearCartCommand, ApiResponse>
{
    private readonly ICartRepository _carts;

    /// <summary>构造处理器。</summary>
    /// <param name="carts">购物车仓储。</param>
    public ClearCartHandler(ICartRepository carts) => _carts = carts;

    /// <summary>执行清空。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(ClearCartCommand request, CancellationToken ct)
    {
        await _carts.ClearAsync(CustomerScope.Require(request.CustomerId), ct);
        return ApiResponseFactory.Ok();
    }
}

/// <summary>查询购物车处理器。</summary>
public sealed class QueryCartHandler : IRequestHandler<QueryCartCommand, ApiResponse<List<CartItemDto>>>
{
    private readonly ICartRepository _carts;

    /// <summary>构造处理器。</summary>
    /// <param name="carts">购物车仓储。</param>
    public QueryCartHandler(ICartRepository carts) => _carts = carts;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>购物车行列表，小计已算好。</returns>
    public async Task<ApiResponse<List<CartItemDto>>> Handle(QueryCartCommand request, CancellationToken ct)
    {
        var items = await _carts.ListAsync(CustomerScope.Require(request.CustomerId), ct);

        var list = items.Select(a => new CartItemDto(
            a.Id.ToString(), a.SkuId, a.ProductId, a.SkuName, a.SkuSpecText,
            a.Price, a.OriginalPrice, a.Image, a.Quantity,
            Math.Round(a.Price * a.Quantity, 2, MidpointRounding.AwayFromZero), a.Checked)).ToList();

        return ApiResults.Ok(list);
    }
}
