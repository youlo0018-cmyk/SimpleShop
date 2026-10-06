using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using PermissionService.Application.Features.Role;

namespace PermissionService.Api.Controllers;

/// <summary>角色管理。所有写操作仅超级管理员，且内置管理员角色受保护。</summary>
[ApiController]
[Route("roles")]
public sealed class RoleController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public RoleController(IMediator mediator) => _mediator = mediator;

    /// <summary>分页查询角色，每项带已绑定权限点数。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数，1-100。</param>
    /// <param name="keyword">按角色名或编码模糊搜索。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>角色列表。</returns>
    [HttpGet("List")]
    public Task<ApiResponse<List<RoleListItem>>> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string keyword = "", CancellationToken ct = default)
        => _mediator.Send(new QueryRolesCommand(page, pageSize, keyword), ct);

    /// <summary>角色下拉（DATA_SPEC 4.2）：只返回启用角色，供建号多选。</summary>
    /// <param name="keyword">按角色名或编码模糊搜索。</param>
    /// <param name="limit">最多返回多少条，1-200。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下拉项 <c>{ value, label, allowedScopes, scopeName }</c>。</returns>
    /// <remarks>
    /// 带 <c>allowedScopes</c>：建号页要按账号类型过滤可选角色，
    /// 否则用户会选到不匹配的角色，保存时才被服务端拒（5.18 作用域校验）。
    /// </remarks>
    [HttpGet("Options")]
    public Task<ApiResponse<List<RoleOption>>> Options(
        [FromQuery] string keyword = "",
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
        => _mediator.Send(new QueryRoleOptionsCommand(keyword, limit), ct);

    /// <summary>查询角色详情与已绑定权限点，用于权限树回显。</summary>
    /// <param name="roleId">角色 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>角色详情。</returns>
    [HttpGet("Detail")]
    public Task<ApiResponse<RoleDetailDto>> Detail([FromQuery] long roleId, CancellationToken ct)
        => _mediator.Send(new QueryRoleDetailCommand(roleId), ct);

    /// <summary>新建角色。仅超级管理员。</summary>
    /// <param name="command">新建命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新角色 Id。</returns>
    [HttpPost("Create")]
    public Task<ApiResponse<long>> Create([FromBody] CreateRoleCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>编辑角色基本信息。仅超级管理员；内置管理员角色会被拒绝。</summary>
    /// <param name="command">编辑命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Update")]
    public Task<ApiResponse> Update([FromBody] UpdateRoleCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>删除角色。仅超级管理员；内置管理员角色会被拒绝，并级联清理绑定。</summary>
    /// <param name="command">删除命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Delete")]
    public Task<ApiResponse> Delete([FromBody] DeleteRoleCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>重绑角色权限点。仅超级管理员；内置管理员角色权限锁定。</summary>
    /// <param name="command">重绑命令，PermissionIds 传空数组表示清空。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("BindPermissions")]
    public Task<ApiResponse> BindPermissions([FromBody] BindRolePermissionsCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
