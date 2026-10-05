using System.Security.Cryptography;
using System.Text;
using Collaboration.Domain.Context;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Collaboration.Web;

/// <summary>
/// 从网关注入的 X-Claim-* 请求头构造 <see cref="TenantContext"/>，供 AOP 过滤与审计字段使用。
/// </summary>
/// <remarks>
/// 踩过的坑：之前根本没有这个中间件，网关注入了 X-Claim-* 也没人读，
/// TenantContextHolder.Current 永远是匿名上下文。后果是
/// SuperAdminBehavior 一律 403（超管做不了权限点/角色的增删改），审计字段也永远是空的。
///
/// 安全边界：这些头<b>只在带对内部口令时</b>才采信。
/// 服务端口可能被绕过网关直接访问，无条件信任头等于开了一个后门。
/// 口令没配时一律按匿名处理并告警——宁可「没人是超管」，也不能「人人都是超管」。
/// </remarks>
public sealed class TenantContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[]? _expectedToken;
    private readonly ILogger<TenantContextMiddleware> _logger;

    /// <summary>构造中间件。</summary>
    /// <param name="next">下一段管道。</param>
    /// <param name="configuration">应用配置，用于读 Tenancy 节。</param>
    /// <param name="logger">日志器，口令缺失或校验失败都要留痕。</param>
    public TenantContextMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<TenantContextMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        var token = configuration.GetSection(TenancyOptions.SectionName)[nameof(TenancyOptions.InternalToken)];
        _expectedToken = string.IsNullOrWhiteSpace(token)
            ? null
            : Encoding.UTF8.GetBytes(token.Trim());

        if (_expectedToken is null)
        {
            _logger.LogWarning(
                "Tenancy:InternalToken 未配置，所有 X-Claim-* 请求头将被忽略，所有请求按匿名处理。" +
                "请在 AgileConfig 补上该配置，否则超管相关接口会一律 403。");
        }
    }

    /// <summary>执行：构造上下文 → 后续管道 → 清理上下文。</summary>
    /// <param name="context">当前请求。</param>
    /// <returns>管道结果。</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        // /health 等探活请求不参与租户判定，避免依赖上下文
        if (IsProbe(context.Request.Path))
        {
            await _next(context);
            return;
        }

        TenantContextHolder.Set(BuildContext(context));

        try
        {
            await _next(context);
        }
        finally
        {
            // 必须清理：AsyncLocal 挂在当前执行上下文上，线程池复用会把上一个请求的租户带过来
            TenantContextHolder.Clear();
        }
    }

    private static bool IsProbe(PathString path)
        => path.StartsWithSegments("/health") || path.StartsWithSegments("/ready");

    /// <summary>按请求头构造租户上下文。</summary>
    /// <param name="http">当前请求。</param>
    /// <returns>租户上下文。</returns>
    private TenantContext BuildContext(HttpContext http)
    {
        var anonymous = new TenantContext { Access = AccessContext.Anonymous };

        if (_expectedToken is null) return anonymous;

        if (!TokenMatches(http)) return anonymous;

        var userId = ReadLong(http, TenancyOptions.Headers.UserId);
        var tenantType = (int)ReadLong(http, TenancyOptions.Headers.TenantType);

        if (userId <= 0 || tenantType is not (1 or 2 or 3)) return anonymous;

        var access = tenantType == 3 ? AccessContext.Customer : AccessContext.Admin;

        return new TenantContext
        {
            Access = access,
            UserId = userId,
            UserName = ReadString(http, TenancyOptions.Headers.UserName),
            CustomerNo = ReadString(http, TenancyOptions.Headers.CustomerNo),
            TenantType = tenantType,
            PlatformId = ReadLong(http, TenancyOptions.Headers.PlatformId),
            MerchantId = ReadLong(http, TenancyOptions.Headers.MerchantId),
            Permissions = ReadList(http, TenancyOptions.Headers.Permissions)
        };
    }

    /// <summary>固定时间比较口令，避免按响应时间逐字节猜。</summary>
    private bool TokenMatches(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue(TenancyOptions.Headers.InternalToken, out var provided))
            return false;

        var text = provided.ToString();
        if (string.IsNullOrEmpty(text)) return false;

        var bytes = Encoding.UTF8.GetBytes(text);
        return CryptographicOperations.FixedTimeEquals(bytes, _expectedToken);
    }

    private static long ReadLong(HttpContext http, string name)
    {
        if (!http.Request.Headers.TryGetValue(name, out var v)) return 0;
        return long.TryParse(v.ToString(), out var result) ? result : 0;
    }

    private static string ReadString(HttpContext http, string name)
        => http.Request.Headers.TryGetValue(name, out var v) ? v.ToString().Trim() : string.Empty;

    /// <summary>读集合型请求头：既支持重复头，也支持单值内逗号分隔。</summary>
    private static IReadOnlyList<string> ReadList(HttpContext http, string name)
    {
        if (!http.Request.Headers.TryGetValue(name, out var values)) return Array.Empty<string>();

        return values
            .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>租户上下文中间件的注册入口，16 个服务共用。</summary>
public static class TenantContextMiddlewareExtensions
{
    /// <summary>把租户上下文中间件插到管道最前面。</summary>
    /// <param name="app">应用构建器。</param>
    /// <returns>原对象，便于链式调用。</returns>
    /// <remarks>
    /// 必须早于异常中间件与控制器：租户判定失败要能在最外层看到，
    /// 而 AOP 在更内层读上下文。
    /// </remarks>
    public static IApplicationBuilder UseAppTenantContext(this IApplicationBuilder app)
        => app.UseMiddleware<TenantContextMiddleware>();
}
