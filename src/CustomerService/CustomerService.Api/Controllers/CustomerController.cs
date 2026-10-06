using Collaboration.Domain.Common;
using CustomerService.Application.Features.Customer.Address;
using CustomerService.Application.Features.Customer.Favorite;
using CustomerService.Application.Features.Customer.Login;
using CustomerService.Application.Features.Customer.Profile;
using CustomerService.Application.Features.Customer.Register;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CustomerService.Api.Controllers;

/// <summary>客户 C 端接口：注册 / 登录 / 资料 / 地址簿 / 收藏。</summary>
/// <remarks>
/// 控制器纯转发：不做业务、不做验证、不查库（CODING_STANDARD 2.1）。
/// 带 <c>customerId</c> 的接口一律在处理器里过 <c>CustomerScope.Require</c> ——
/// 客户令牌与请求体里的客户不一致时返回 403，防止拿别人的 Id 读改别人的资料 / 地址 / 收藏。
/// </remarks>
[ApiController]
[Route("customers")]
public sealed class CustomerController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public CustomerController(IMediator mediator) => _mediator = mediator;

    /// <summary>客户注册，游客可调用。</summary>
    /// <param name="command">注册命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回客户信息与令牌；登录名或手机号已占用返回 400。</returns>
    [HttpPost("Register")]
    public Task<ApiResponse<RegisterResult>> Register([FromBody] RegisterCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>客户登录，游客可调用。</summary>
    /// <param name="command">登录命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回令牌与资料；凭据错误返回 400 与统一消息。</returns>
    [HttpPost("Login")]
    public Task<ApiResponse<LoginResult>> Login([FromBody] LoginCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>查自己的资料（「我的」页）。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>资料视图；手机号打码下发。</returns>
    [HttpPost("Profile")]
    public Task<ApiResponse<CustomerProfileDto>> Profile(
        [FromBody] QueryCustomerProfileCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>改自己的资料（昵称 / 头像 / 性别 / 生日）。</summary>
    /// <param name="command">更新命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>更新后的资料。</returns>
    /// <remarks>登录名与手机号不在这里改：它们要么是唯一键，要么需要额外验证。</remarks>
    [HttpPost("UpdateProfile")]
    public Task<ApiResponse<CustomerProfileDto>> UpdateProfile(
        [FromBody] UpdateCustomerProfileCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>地址簿分页（默认地址优先）。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>地址分页。</returns>
    [HttpPost("addresses/List")]
    public Task<ApiResponse<PagedResult<CustomerAddressDto>>> ListAddresses(
        [FromBody] QueryCustomerAddressesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>新增收货地址。</summary>
    /// <param name="command">新增命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新地址 Id。</returns>
    /// <remarks>第一条地址自动成为默认地址。</remarks>
    [HttpPost("addresses/Create")]
    public Task<ApiResponse<string>> CreateAddress(
        [FromBody] CreateCustomerAddressCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>修改收货地址。</summary>
    /// <param name="command">更新命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>别人的地址返回 404（等同于不存在），避免泄露「这个 Id 真实存在」。</remarks>
    [HttpPost("addresses/Update")]
    public Task<ApiResponse> UpdateAddress(
        [FromBody] UpdateCustomerAddressCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>删除收货地址（软删）。</summary>
    /// <param name="command">删除命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>删掉默认地址时，剩下最新的一条自动顶上默认。</remarks>
    [HttpPost("addresses/Delete")]
    public Task<ApiResponse> DeleteAddress(
        [FromBody] DeleteCustomerAddressCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>设为默认地址。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("addresses/SetDefault")]
    public Task<ApiResponse> SetDefaultAddress(
        [FromBody] SetDefaultCustomerAddressCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>收藏夹分页（按收藏时间倒序）。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>收藏分页，只含商品 Id 与收藏时间。</returns>
    [HttpPost("favorites/List")]
    public Task<ApiResponse<PagedResult<CustomerFavoriteDto>>> ListFavorites(
        [FromBody] QueryCustomerFavoritesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>收藏商品（重复收藏幂等）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应；超过上限返回额度不足。</returns>
    [HttpPost("favorites/Add")]
    public Task<ApiResponse> AddFavorite(
        [FromBody] AddCustomerFavoriteCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>取消收藏（未收藏也返回成功）。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("favorites/Remove")]
    public Task<ApiResponse> RemoveFavorite(
        [FromBody] RemoveCustomerFavoriteCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}

