using Collaboration.Domain.Context;
using Collaboration.Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace PermissionService.Application.Security;

/// <summary>
/// 标记「仅超级管理员可执行」的请求。
/// </summary>
/// <remarks>
/// 依据 DATA_SPEC 5.22：权限点与角色管理的 read/create/update/delete 四个权限点
/// **额外要求 PlatformId = 0**，网关之外还要在服务端二次校验。
/// 用管道行为统一拦，而不是每个 Handler 各写一遍——漏一个就是一个越权口子。
/// </remarks>
public interface ISuperAdminOnly
{
    /// <summary>请求说明，仅用于日志。</summary>
    string AuditNote { get; }
}

/// <summary>超级管理员管道行为：非超管直接拒绝，且不进入 Handler。</summary>
public sealed class SuperAdminBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<SuperAdminBehavior<TRequest, TResponse>> _logger;

    /// <summary>构造行为。</summary>
    /// <param name="logger">日志器，越权尝试要留痕。</param>
    public SuperAdminBehavior(ILogger<SuperAdminBehavior<TRequest, TResponse>> logger) => _logger = logger;

    /// <summary>校验并放行。</summary>
    /// <param name="request">请求。</param>
    /// <param name="next">下一段管道。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下一段的执行结果。</returns>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        // 只拦实现了 ISuperAdminOnly 的请求，权限树查询等普通请求照常放行
        if (request is not ISuperAdminOnly) return await next();

        var ctx = TenantContextHolder.Current;

        if (!ctx.IsSuperAdmin)
        {
            _logger.LogWarning(
                "越权尝试：平台 {PlatformId} 账号 {UserId} 执行了仅超管可做的 {Request}",
                ctx.PlatformId, ctx.UserId, typeof(TRequest).Name);

            throw new BaseApiException(BaseApiResponseCode.Forbidden, "该操作仅限超级管理员");
        }

        return await next();
    }
}

