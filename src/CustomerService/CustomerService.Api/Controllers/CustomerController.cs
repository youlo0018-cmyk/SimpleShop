using Collaboration.Domain.Common;
using CustomerService.Application.Features.Customer.Login;
using CustomerService.Application.Features.Customer.Register;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CustomerService.Api.Controllers;

/// <summary>客户注册与登录。控制器纯转发：不做业务、不做验证、不查库（CODING_STANDARD 2.1）。</summary>
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
}

