using Collaboration.Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using UserService.Application.Features.Internal;

namespace UserService.Api.Controllers;

/// <summary>内部服务间接口，仅供 AuthService 调用，<b>网关不路由 /internal 前缀</b>。</summary>
/// <remarks>
/// 安全边界靠网关而不是靠接口自身鉴权：认证中心必须先能校验凭据，
/// 就没法再用「已登录才能调」来保护这个接口——鸡生蛋问题。
/// 因此约定：网关的 Ocelot 路由表里不出现 /internal/**，
/// 外部请求到不了这里；服务本身不监听公网，只在容器内网可达。
/// 待 Gateway 落地时要在路由配置文件里确认 /internal 前缀确实缺失，并写成自动化断言。
/// </remarks>
[ApiController]
[Route("internal/users")]
public sealed class InternalUserController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>构造控制器。</summary>
    /// <param name="mediator">MediatR 入口。</param>
    public InternalUserController(IMediator mediator) => _mediator = mediator;

    /// <summary>校验后台账号凭据。</summary>
    /// <param name="command">登录名与明文密码。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回账号身份（不含哈希与角色）；失败返回 400。</returns>
    [HttpPost("Authenticate")]
    public Task<ApiResponse<AdminIdentity>> Authenticate([FromBody] AuthenticateAdminCommand command, CancellationToken ct)
        => _mediator.Send(command, ct);
}