using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using PermissionService.Application.Features.Permission.CreatePermission;
using PermissionService.Application.Features.Permission.DeletePermission;
using PermissionService.Application.Features.Permission.QueryTree;
using PermissionService.Application.Features.Permission.UpdatePermission;

namespace PermissionService.Api.Controllers;

/// <summary>权限点与权限树。控制器纯转发（CODING_STANDARD 2.1）。</summary>
[ApiController]
[Route("permissions")]
public sealed class PermissionController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public PermissionController(IMediator mediator) => _mediator = mediator;

    /// <summary>查询权限树，4 层结构，最外层是虚拟根节点「全部权限」。</summary>
    /// <param name="includeDisabled">是否包含已停用权限点，默认 false。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>权限树。节点含 selectable，前端据此禁止勾选空模块。</returns>
    [HttpGet("Tree")]
    public Task<ApiResponse<List<PermissionNodeDto>>> Tree([FromQuery] bool includeDisabled = false, CancellationToken ct = default)
        => _mediator.Send(new QueryPermissionTreeCommand(includeDisabled), ct);

    /// <summary>新建权限点。仅超级管理员。</summary>
    /// <param name="command">新建命令，ParentId 传 0 表示一级业务大类。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新权限点 Id。</returns>
    [HttpPost("Create")]
    public Task<ApiResponse<long>> Create([FromBody] CreatePermissionCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>编辑权限点。仅超级管理员；内置权限点只能停用。</summary>
    /// <param name="command">编辑命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>编辑结果。</returns>
    [HttpPost("Update")]
    public Task<ApiResponse> Update([FromBody] UpdatePermissionCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>删除权限点。仅超级管理员；内置权限点会被拒绝。</summary>
    /// <param name="command">删除命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Delete")]
    public Task<ApiResponse> Delete([FromBody] DeletePermissionCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>启用 / 停用权限点。仅超级管理员；内置权限点只能停用。</summary>
    /// <param name="command">状态变更命令，Status 为 1 启用 或 2 停用。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Status")]
    public Task<ApiResponse> Status([FromBody] ChangePermissionStatusCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
