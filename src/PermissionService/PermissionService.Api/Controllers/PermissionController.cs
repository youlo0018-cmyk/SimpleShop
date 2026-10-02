using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using PermissionService.Application.Features.Permission.QueryTree;

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
    /// <returns>权限树。节点只含中文名，不返回编码给界面直接展示（DESIGN_SPEC 6）。</returns>
    [HttpGet("Tree")]
    public Task<ApiResponse<List<PermissionNodeDto>>> Tree([FromQuery] bool includeDisabled = false, CancellationToken ct = default)
        => _mediator.Send(new QueryPermissionTreeCommand(includeDisabled), ct);
}

