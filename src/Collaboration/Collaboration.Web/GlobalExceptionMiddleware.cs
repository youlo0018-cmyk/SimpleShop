using System.Net;
using System.Text.Json;
using Collaboration.Domain.Common;
using Collaboration.Domain.Messaging;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Collaboration.Web;

/// <summary>全局异常中间件：把异常翻译成统一响应体，不再让任何异常裸奔成 500。</summary>
/// <remarks>
/// 链路位置：必须注册在管道最前面、路由之前（CODING_STANDARD 3.3）。
/// 映射：ValidationException 转 400 带字段级 errors；其他异常转 500 且**详细信息只写日志不回前端**，
/// 因为堆栈与连接串属于信息泄露。依据 CODING_STANDARD.md 3.3。
/// </remarks>
public sealed class GlobalExceptionMiddleware
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly IEventPublisher _events;
    private readonly string _serviceName;

    /// <summary>构造中间件。</summary>
    /// <param name="next">下一段管道。</param>
    /// <param name="logger">日志器，异常详情只写这里。</param>
    /// <param name="events">事件发布端口，用于发出 exception.log。</param>
    /// <param name="configuration">应用配置，用于读服务名。</param>
    public GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger,
        IEventPublisher events,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _events = events;
        _serviceName = configuration["Consul:ServiceName"]
            ?? configuration["Service:Name"]
            ?? "UnknownService";
    }

    /// <summary>处理请求，捕获并转换异常。</summary>
    /// <param name="context">当前请求上下文。</param>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ValidationException ex)
        {
            _logger.LogInformation(ex, "请求 {Path} 参数校验失败", context.Request.Path);
            var errors = ex.Errors
                .GroupBy(e => e.PropertyName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray(), StringComparer.OrdinalIgnoreCase);
            await WriteAsync(context, HttpStatusCode.BadRequest, "请求参数校验失败", errors);
        }
        catch (BaseApiException ex)
        {
            _logger.LogInformation(ex, "请求 {Path} 业务拒绝：{Message}", context.Request.Path, ex.Message);
            await WriteAsync(context, (HttpStatusCode)ex.Code, ex.Message, ex.Errors);
        }

        catch (Exception ex)
        {
            _logger.LogError(ex, "请求 {Path} 未处理异常", context.Request.Path);

            // 业务拒绝与参数校验失败**不**记 exception.log：
            // 它们是正常流程的一部分（余额不足、字段没填对），
            // 全记成「未处理异常」会让异常列表没法用——
            // 真正的故障会被淹没在用户输错东西的噪音里。
            await PublishExceptionAsync(context, ex).ConfigureAwait(false);

            await WriteAsync(context, HttpStatusCode.InternalServerError, "服务器内部错误，请稍后重试", null);
        }
    }

    /// <summary>发布 exception.log。</summary>
    /// <param name="context">当前请求。</param>
    /// <param name="ex">异常。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 失败只写日志、绝不抛：这里正在处理「已经出错了」的请求，
    /// 再抛一次异常会把错误响应也顶掉，用户拿到的是连接重置而不是 500。
    /// </remarks>
    private async Task PublishExceptionAsync(HttpContext context, Exception ex)
    {
        try
        {
            var entry = new ExceptionLogEntry(
                DateTime.UtcNow,
                _serviceName,
                context.Request.Path.Value ?? "/",
                context.Request.Method,
                ex.GetType().FullName ?? ex.GetType().Name,
                ex.Message,
                ex.StackTrace ?? string.Empty,
                context.TraceIdentifier);

            await _events.PublishAsync(EventTopics.ExceptionLog, entry).ConfigureAwait(false);
        }
        catch (Exception publishEx) when (publishEx is not OperationCanceledException)
        {
            _logger.LogWarning(publishEx, "发布 exception.log 失败（不影响错误响应）");
        }
    }

    private static async Task WriteAsync(HttpContext context, HttpStatusCode status, string message, IDictionary<string, string[]>? errors)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";
        var code = status == HttpStatusCode.BadRequest ? BaseApiResponseCode.BadRequest : BaseApiResponseCode.InternalError;
        await context.Response.WriteAsync(JsonSerializer.Serialize(ApiResults.Fail<object>(code, message, errors), JsonOpts));
    }
}

