using Collaboration.Domain.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Collaboration.Web;

/// <summary>控制器注册的统一入口，所有 HTTP 服务共用。</summary>
/// <remarks>
/// 存在的理由：<c>[ApiController]</c> 的模型绑定失败（缺必填字段、JSON 格式错误）不会走
/// MediatR 的 FluentValidation 管道，而是由 MVC 自己短路成 400。若不接管，
/// 前端会收到框架自带的英文提示（例如「The Password field is required.」）。
/// 这里把这类提示统一翻译成中文，并保持与 <c>GlobalExceptionMiddleware</c> 相同的响应体形状。
/// </remarks>
public static class AppControllersExtensions
{
    /// <summary>注册控制器并统一模型绑定失败的中文响应。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>MVC 构建器，便于调用方继续链式配置。</returns>
    public static IMvcBuilder AddAppControllers(this IServiceCollection services)
        => services.AddControllers().ConfigureApiBehaviorOptions(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var errors = context.ModelState
                    .Where(pair => pair.Value?.Errors.Count > 0)
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value!.Errors
                            .Select(error => TranslateModelError(error.ErrorMessage))
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(),
                        StringComparer.OrdinalIgnoreCase);

                foreach (var key in errors.Keys.ToArray())
                {
                    if (errors[key].Length == 0)
                    {
                        errors[key] = new[] { "填写内容不正确" };
                    }
                }

                return new BadRequestObjectResult(
                    ApiResults.Fail(BaseApiResponseCode.BadRequest, "请求参数校验失败", errors));
            };
        });

    /// <summary>把 MVC 模型绑定错误翻译成面向用户的中文提示。</summary>
    /// <param name="message">框架原始错误消息。</param>
    /// <returns>中文提示；无法识别时保留原文。</returns>
    private static string TranslateModelError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "填写内容不正确";
        }

        if (message.Contains("field is required", StringComparison.OrdinalIgnoreCase)
            || message.Contains("is required", StringComparison.OrdinalIgnoreCase))
        {
            return "不能为空";
        }

        if (message.Contains("could not be converted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("JSON", StringComparison.OrdinalIgnoreCase)
            || message.Contains("deserialize", StringComparison.OrdinalIgnoreCase))
        {
            return "格式不正确";
        }

        return message;
    }
}
