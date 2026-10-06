using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using PermissionService.Application.Features.Internal;

namespace PermissionService.Api.Controllers;

/// <summary>内部服务间接口，仅供 AuthService / UserService 调用，网关不路由 /internal 前缀。</summary>
/// <remarks>
/// 与 UserService 的 /internal 同一条边界约定：认证中心必须在「还没拿到令牌」时
/// 就能查到权限，所以这些接口没法用「已登录才能调」保护自己。
/// 靠网关路由表里不出现 /internal/** 来隔离，服务只监听内网地址。
/// Gateway 落地时要确认这一点，并写成自动化断言而不是靠人记得。
/// </remarks>
[ApiController]
[Route("internal/permissions")]
public sealed class InternalPermissionController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public InternalPermissionController(IMediator mediator) => _mediator = mediator;

    /// <summary>解析账号的角色与权限点。</summary>
    /// <param name="command">账号 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>权限点编码集合与角色列表；无绑定时两者都是空（fail-closed）。</returns>
    [HttpPost("Resolve")]
    public Task<ApiResponse<ResolvedPermissions>> Resolve([FromBody] ResolvePermissionsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>重绑账号的角色集合。</summary>
    /// <param name="command">账号 Id、目标角色 Id 集合与平台 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回受影响行数；RoleIds 传空数组表示解绑全部。</returns>
    [HttpPost("BindUserRoles")]
    public Task<ApiResponse<int>> BindUserRoles([FromBody] BindUserRolesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>预检角色集合能否用于指定租户类型的账号。</summary>
    /// <param name="command">角色 Id 集合与目标账号租户类型。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>通过返回成功；角色不存在或作用域不匹配返回 400 与原因。</returns>
    /// <remarks>UserService 在建号插库之前调用，避免出现「返回成功但账号没角色」的半成品。</remarks>
    [HttpPost("ValidateRoleScopes")]
    public Task<ApiResponse<string>> ValidateRoleScopes(
        [FromBody] ValidateRoleScopesCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>查询网关 RBAC 用的「接口路径 → 权限点」映射。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>全部启用且绑定了路径的权限点。网关缓存 30 秒。</returns>
    /// <remarks>
    /// 刻意用 GET：网关启动与每 30 秒的缓存刷新都要拉它，语义上是幂等只读，
    /// 也不会因为被当成写操作而被什么「只允许 POST」的网关策略拦住。
    /// </remarks>
    [HttpGet("RouteMap")]
    public Task<ApiResponse<List<RouteMapEntry>>> RouteMap(CancellationToken ct)
        => _mediator.Send(new GetRouteMapCommand(), ct);
}
