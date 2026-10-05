using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Collaboration.Domain.MediatR;

/// <summary>标记「仅超级管理员可执行」的请求。</summary>
/// <remarks>
/// 「超管专属」是一类**跨服务**的规则，不只权限服务有：
/// 权限点与角色的增删改（DATA_SPEC 5.22）、新建平台（平台由超管添加）
/// 都属于这一类。所以判定放在共享库里，而不是各服务各写一份。
///
/// <para>为什么需要它在**网关之外**再拦一次：网关的 RBAC 只回答
/// 「你有没有这个权限点」。而「有 platform:create 权限点的账号也可能不是超管」
/// 是完全可能的 —— 权限点是可运行时配置的实体，某天有人把 platform:create
/// 勾给了一个平台角色，网关就会放行。超管判定是**不看权限点、只看租户身份**的第二道闸。</para>
/// </remarks>
public interface ISuperAdminOnly
{
    /// <summary>请求说明，仅用于日志与排障。</summary>
    string AuditNote { get; }
}

/// <summary>超级管理员管道行为：非超管直接拒绝，且不进入 Handler。</summary>
public sealed class SuperAdminBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<SuperAdminBehavior<TRequest, TResponse>> _logger;

    /// <summary>构造行为。</summary>
    /// <param name="logger">日志器。越权尝试要留痕。</param>
    public SuperAdminBehavior(ILogger<SuperAdminBehavior<TRequest, TResponse>> logger) => _logger = logger;

    /// <summary>校验并放行。</summary>
    /// <param name="request">请求。</param>
    /// <param name="next">下一段管道。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下一段的执行结果。</returns>
    /// <exception cref="BaseApiException">调用方不是超管时抛出 403。</exception>
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        // 只拦实现了 ISuperAdminOnly 的请求：权限树查询、平台列表这类普通请求照常放行。
        if (request is not ISuperAdminOnly marked) return await next();

        var ctx = TenantContextHolder.Current;

        if (!ctx.IsSuperAdmin)
        {
            _logger.LogWarning(
                "越权尝试：平台 {PlatformId} 账号 {UserId} 执行了仅超管可做的 {Request}（{Note}）",
                ctx.PlatformId, ctx.UserId, typeof(TRequest).Name, marked.AuditNote);

            throw new BaseApiException(BaseApiResponseCode.Forbidden, "该操作仅限超级管理员");
        }

        return await next();
    }
}
