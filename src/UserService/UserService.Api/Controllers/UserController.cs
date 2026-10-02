using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using UserService.Application.Features.User.ManageUser;

namespace UserService.Api.Controllers;

/// <summary>后台账号管理。租户裁剪由查询参数传入，不依赖隐式过滤。</summary>
[ApiController]
[Route("users")]
public sealed class UserController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public UserController(IMediator mediator) => _mediator = mediator;

    /// <summary>分页查询后台账号。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数，1-100。</param>
    /// <param name="keyword">按登录名或昵称模糊搜索。</param>
    /// <param name="platformId">平台 Id，0 表示不限（仅超管）。</param>
    /// <param name="merchantId">商户 Id，0 表示不限。</param>
    /// <param name="status">状态，0 表示不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>账号列表。</returns>
    [HttpGet("List")]
    public Task<ApiResponse<List<UserListItem>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string keyword = "",
        [FromQuery] long platformId = 0,
        [FromQuery] long merchantId = 0,
        [FromQuery] int status = 0,
        CancellationToken ct = default)
        => _mediator.Send(new QueryUsersCommand(page, pageSize, keyword, platformId, merchantId, status), ct);

    /// <summary>新建后台账号。租户类型只能是 1 平台 或 2 商户，禁止客户类型。</summary>
    /// <param name="command">建号命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新账号 Id。</returns>
    [HttpPost("Create")]
    public Task<ApiResponse<long>> Create([FromBody] CreateUserCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>编辑后台账号基本信息。租户类型与所属平台 / 商户不可在此修改。</summary>
    /// <param name="command">编辑命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("Update")]
    public Task<ApiResponse> Update([FromBody] UpdateUserCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>重置密码。后台直接设置新密码，不需要旧密码。</summary>
    /// <param name="command">重置命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("ResetPassword")]
    public Task<ApiResponse> ResetPassword([FromBody] ResetPasswordCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>启用 / 停用账号。停用只挡新登录，已签发令牌仍有效到过期。</summary>
    /// <param name="command">状态变更命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    [HttpPost("UpdateStatus")]
    public Task<ApiResponse> UpdateStatus([FromBody] ChangeUserStatusCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}