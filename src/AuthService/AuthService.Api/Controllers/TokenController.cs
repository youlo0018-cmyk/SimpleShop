using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Api.Controllers;

/// <summary>OAuth2 / OpenID Connect 令牌端点。</summary>
/// <remarks>
/// 为什么密码流要落到控制器里，而不是纯用 OpenIddict 处理器：
/// 令牌端点开了 EnableTokenEndpointPassthrough 之后，OpenIddict 只负责校验请求，
/// 真正的签发由这个控制器调 SignIn(...) 完成——这是 OpenIddict 官方密码流的推荐写法。
///
/// 凭据校验发生在更早的 ValidateAdminPasswordGrantHandler（ValidateTokenRequestContext 阶段），
/// 它把验好的主体放进 Transaction.Properties；本控制器通过
/// HttpContext.Features 上的 OpenIddictServerAspNetCoreFeature 取到同一个 Transaction 再取出来签发。
///
/// 注意 OpenIddict 7 里没有 GetOpenIddictServerRequest() 这个扩展方法了，
/// 请求与事务都要从 Feature 上拿——照着旧版文章写会直接编译不过。
///
/// 路由用 ~/connect/token 绝对路径：地址由 SetTokenEndpointUris("connect/token") 声明，
/// 控制器必须与它一致。
/// </remarks>
[ApiController]
public sealed class TokenController : ControllerBase
{
    /// <summary>交换令牌。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>签发成功返回令牌；取不到已校验主体时返回 OAuth2 错误 JSON。</returns>
    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public IActionResult Exchange(CancellationToken ct)
    {
        var transaction = HttpContext.Features.Get<OpenIddictServerAspNetCoreFeature>()?.Transaction;
        if (transaction is null)
        {
            throw new InvalidOperationException(
                "OpenIddict 事务不存在，说明请求没有经过令牌端点。路由或端点配置可能改错了。");
        }

        var request = transaction.Request ?? throw new InvalidOperationException("OpenIddict 请求为空。");
        if (!string.Equals(request.GrantType, GrantTypes.Password, StringComparison.Ordinal))
        {
            return Error(Errors.UnsupportedGrantType, "This endpoint only supports the password grant type.");
        }

        // 校验阶段没放主体进来 = 凭据没通过（它已经 Reject 过了）。
        // 这里必须直接返回错误，绝不能回落到「发一个匿名令牌」——
        // 那等于任何密码都能换到令牌，是最严重的鉴权漏洞。
        if (!transaction.Properties.TryGetValue(ValidateAdminPasswordGrantHandler.PrincipalKey, out var value)
            || value is not ClaimsPrincipal principal)
        {
            return Error(Errors.InvalidGrant, "Invalid username or password.");
        }

        transaction.Properties.Remove(ValidateAdminPasswordGrantHandler.PrincipalKey);
        if (ct.IsCancellationRequested) return StatusCode(StatusCodes.Status503ServiceUnavailable);

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>构造 OAuth2 错误响应。</summary>
    /// <param name="error">标准错误码。</param>
    /// <param name="description">错误说明，必须是 ASCII（会进 WWW-Authenticate 头）。</param>
    /// <returns>400 且 body 为标准 OAuth2 错误 JSON。</returns>
    private IActionResult Error(string error, string description) => new ObjectResult(new Dictionary<string, object>
    {
        ["error"] = error,
        ["error_description"] = description
    })
    {
        StatusCode = StatusCodes.Status400BadRequest,
        ContentTypes = { "application/json" }
    };
}