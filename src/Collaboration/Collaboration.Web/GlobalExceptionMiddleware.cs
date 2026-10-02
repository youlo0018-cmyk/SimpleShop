using System.Net;
using System.Text.Json;
using Collaboration.Domain.Common;
using FluentValidation;
using Microsoft.AspNetCore.Http;
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

    /// <summary>构造中间件。</summary>
    /// <param name="next">下一段管道。</param>
    /// <param name="logger">日志器，异常详情只写这里。</param>
    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "请求 {Path} 未处理异常", context.Request.Path);
            await WriteAsync(context, HttpStatusCode.InternalServerError, "服务器内部错误，请稍后重试", null);
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

