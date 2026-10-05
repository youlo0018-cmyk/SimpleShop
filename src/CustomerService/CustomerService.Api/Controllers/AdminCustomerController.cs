using Collaboration.Domain.Common;
using CustomerService.Application.Features.Customer.Admin;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CustomerService.Api.Controllers;

/// <summary>后台客户管理。控制器纯转发，不做业务、不做验证、不查库（CODING_STANDARD 2.1）。</summary>
/// <remarks>
/// 上游刻意是 <c>/admin/customers</c> 而不是 <c>/customers</c>：
/// C 端的 <c>/customers/*</c> 是游客可调的（注册 / 登录），
/// 混在一个前缀下会让「哪些接口要登录」这件事在网关权限配置里变得难以分辨。
/// </remarks>
[ApiController]
[Route("admin/customers")]
public sealed class AdminCustomerController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public AdminCustomerController(IMediator mediator) => _mediator = mediator;

    /// <summary>客户分页。可按状态与关键字筛选。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>客户分页结果。</returns>
    [HttpPost("List")]
    public Task<ApiResponse<PagedResult<AdminCustomerDto>>> List(
        [FromBody] QueryAdminCustomersCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>客户详情。</summary>
    /// <param name="command">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>客户详情。</returns>
    [HttpPost("Detail")]
    public Task<ApiResponse<AdminCustomerDto>> Detail(
        [FromBody] QueryAdminCustomerDetailCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);

    /// <summary>启用 / 停用客户。停用后该客户<b>不能登录</b>。</summary>
    /// <param name="command">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应；状态已被别人改过返回业务错误。</returns>
    [HttpPost("ChangeStatus")]
    public Task<ApiResponse> ChangeStatus([FromBody] ChangeCustomerStatusCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}
