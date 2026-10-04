using System.Diagnostics;
using Collaboration.Domain.Context;
using Collaboration.Domain.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Collaboration.Web;

/// <summary>
/// 请求日志中间件：每个请求发一条 pv.log，写方法额外发一条 operation.log。
/// </summary>
/// <remarks>
/// <para><b>发事件而不是直接写 ES</b>：日志服务挂了不该让业务请求失败，
/// 而 <see cref="IEventPublisher.PublishAsync{T}"/> 在未配置 MQ 时返回 false、
/// 发送失败也只返回 false，天然满足这一点。</para>
///
/// <para><b>探活请求不发</b>：/health 每几秒被调一次，全记下来的话
/// 日志里 99% 都是探活记录，真出事时一眼看不到。</para>
///
/// <para><b>响应已开始就不能再改响应</b>：所以异常路径交给
/// <see cref="GlobalExceptionMiddleware"/> 自己发 exception.log，
/// 本中间件只管「正常走完」的请求。</para>
/// </remarks>
public sealed class RequestLogMiddleware
{
    /// <summary>会被当成「写操作」的方法。</summary>
    private static readonly HashSet<string> WriteMethods =
        new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    private readonly RequestDelegate _next;
    private readonly IEventPublisher _events;
    private readonly string _serviceName;
    private readonly ILogger<RequestLogMiddleware> _logger;

    /// <summary>构造中间件。</summary>
    /// <param name="next">下一段管道。</param>
    /// <param name="events">事件发布端口。</param>
    /// <param name="configuration">应用配置，用于读服务名。</param>
    /// <param name="logger">日志器。</param>
    public RequestLogMiddleware(
        RequestDelegate next,
        IEventPublisher events,
        IConfiguration configuration,
        ILogger<RequestLogMiddleware> logger)
    {
        _next = next;
        _events = events;
        _logger = logger;

        // 服务名从配置来。取不到就用程序集名兜底，
        // 硬编码一个名字的话，同一份镜像部署成两个服务时日志就分不清了。
        _serviceName = configuration["Consul:ServiceName"]
            ?? configuration["Service:Name"]
            ?? "UnknownService";
    }

    /// <summary>执行请求并记录日志。</summary>
    /// <param name="context">当前请求。</param>
    /// <returns>管道结果。</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsProbe(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var started = Stopwatch.StartNew();

        // 🔴 异常必须继续往外抛：吞掉它会让下游返回 200，
        // 全局异常中间件也收不到，日志里就只剩一条「200 的 pv」，
        // 排查时完全看不出这里其实报错了。
        await _next(context).ConfigureAwait(false);

        started.Stop();

        await PublishAsync(context, started.ElapsedMilliseconds).ConfigureAwait(false);
    }

    /// <summary>发布 pv.log 与 operation.log。</summary>
    /// <param name="context">当前请求。</param>
    /// <param name="elapsedMs">耗时毫秒。</param>
    /// <returns>异步任务。</returns>
    private async Task PublishAsync(HttpContext context, long elapsedMs)
    {
        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? "/";
        var requestId = ResolveRequestId(context);

        var pv = new PvLogEntry(
            DateTime.UtcNow,
            _serviceName,
            path,
            context.Request.QueryString.HasValue ? context.Request.QueryString.Value!.TrimStart('?') : string.Empty,
            method,
            context.Response.StatusCode,
            elapsedMs,
            ResolveClientIp(context),
            context.Request.Headers.UserAgent.ToString(),
            requestId);

        await SafePublishAsync(EventTopics.PvLog, pv).ConfigureAwait(false);

        if (!WriteMethods.Contains(method)) return;

        var tenant = TenantContextHolder.Current;

        var operation = new OperationLogEntry(
            DateTime.UtcNow,
            _serviceName,
            tenant.UserId,
            tenant.UserName ?? string.Empty,
            tenant.Access == AccessContext.Admin,
            method,
            path,
            context.Response.StatusCode,
            requestId,
            elapsedMs);

        await SafePublishAsync(EventTopics.OperationLog, operation).ConfigureAwait(false);
    }

    /// <summary>发布一条事件，失败只写日志。</summary>
    /// <typeparam name="T">载荷类型。</typeparam>
    /// <param name="topic">事件类型。</param>
    /// <param name="payload">载荷。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 这里**必须吞掉异常**：日志是旁路，让它把已经成功的业务请求拖成 500
    /// 是本末倒置。<see cref="IEventPublisher"/> 实现本身已经保证不抛，
    /// 这一层是防将来有人换实现时把异常漏出来。
    /// </remarks>
    private async Task SafePublishAsync<T>(string topic, T payload)
    {
        try
        {
            await _events.PublishAsync(topic, payload).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "发布 {Topic} 失败（不影响主流程）", topic);
        }
    }

    /// <summary>取请求 Id。</summary>
    /// <param name="context">当前请求。</param>
    /// <returns>请求 Id。</returns>
    /// <remarks>
    /// 优先用网关注入的 X-Correlation-Id：一次请求会经过网关再转发到下游，
    /// 只有它能把「网关那条 pv」和「下游那条 pv」串成同一次调用。
    /// 没有它就退回 ASP.NET Core 自己的 TraceIdentifier。
    /// </remarks>
    private static string ResolveRequestId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Correlation-Id", out var correlation)
            && !string.IsNullOrWhiteSpace(correlation))
        {
            return correlation.ToString();
        }

        return context.TraceIdentifier;
    }

    /// <summary>取客户端 IP。</summary>
    /// <param name="context">当前请求。</param>
    /// <returns>IP 字符串。</returns>
    /// <remarks>
    /// 必须剥掉端口（IPv6 的 <c>[::1]:12345</c> 形式带方括号和端口），
    /// 否则按 IP 聚合日志时同一个客户端会算成好几个。
    /// </remarks>
    private static string ResolveClientIp(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null) return string.Empty;

        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
    }

    /// <summary>是不是探活 / 静态资源请求。</summary>
    /// <param name="path">请求路径。</param>
    /// <returns>是则返回 true。</returns>
    private static bool IsProbe(PathString path)
        => path.StartsWithSegments("/health")
        || path.StartsWithSegments("/ready");
}

/// <summary>请求日志中间件的注册入口，所有服务共用。</summary>
public static class RequestLogMiddlewareExtensions
{
    /// <summary>把请求日志中间件插到管道里。</summary>
    /// <param name="app">应用构建器。</param>
    /// <returns>原对象，便于链式调用。</returns>
    /// <remarks>
    /// 必须排在 <see cref="GlobalExceptionMiddlewareExtensions.UseAppExceptionHandling"/> <b>之后</b>：
    /// 这样它统计到的 StatusCode 才是「异常被翻译之后的真实响应码」，
    /// 而不是 500 之前的中间状态。
    /// </remarks>
    public static IApplicationBuilder UseAppRequestLogging(this IApplicationBuilder app)
        => app.UseMiddleware<RequestLogMiddleware>();
}
