using Microsoft.AspNetCore.Builder;

namespace Collaboration.Web;

/// <summary>全局异常中间件的注册入口，16 个服务共用。</summary>
public static class GlobalExceptionMiddlewareExtensions
{
    /// <summary>把全局异常中间件插到管道最前面。</summary>
    /// <param name="app">应用构建器。</param>
    /// <returns>原对象，便于链式调用。</returns>
    /// <remarks>必须早于 UseRouting / UseAuthorization，否则控制器与过滤器抛的异常会漏掉。</remarks>
    public static IApplicationBuilder UseAppExceptionHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<GlobalExceptionMiddleware>();
}

