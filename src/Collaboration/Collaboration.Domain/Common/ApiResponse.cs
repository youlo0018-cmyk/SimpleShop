namespace Collaboration.Domain.Common;

/// <summary>
/// 统一响应体。网关与所有服务共用。
/// </summary>
/// <typeparam name="T">业务数据类型。</typeparam>
/// <remarks>
/// 链路位置：Handler 构造（通过 ApiResults），控制器直接返回。
/// 禁止事项：Handler 返回类型已是 ApiResponse 时，控制器**不得再包一层 Ok()**——
/// 那会产生双层信封 { data: { code, message, data } }，前端解包后读不到 code 与 success
/// （CODING_STANDARD.md 2.2）。
/// </remarks>
public sealed class ApiResponse<T>
{
    /// <summary>是否成功。</summary>
    public bool Success { get; set; }

    /// <summary>响应码，见 <see cref="BaseApiResponseCode"/>。</summary>
    public int Code { get; set; }

    /// <summary>
    /// 提示消息。
    /// </summary>
    /// <remarks>
    /// 面向用户的**中文完整句**，不出现代码字段名与英文枚举
    /// （DESIGN_SPEC.md 5.6 文案规范）。
    /// </remarks>
    public string Message { get; set; } = string.Empty;

    /// <summary>业务数据。失败时为 null。</summary>
    public T? Data { get; set; }

    /// <summary>
    /// 字段级错误集合，key 为字段名，value 为该字段的失败原因列表。
    /// </summary>
    /// <remarks>
    /// Validator 失败时由 FluentValidation 的 errors 转换而来。
    /// 前端只用它做 tip 提示，**不飘红输入框**（CODING_STANDARD.md 3.4）。
    /// </remarks>
    public IDictionary<string, string[]> Errors { get; set; } = new Dictionary<string, string[]>();
}


/// <summary>无数据负载的统一响应体。DATA_SPEC 3.5 定义的基础形态。</summary>
public class ApiResponse
{
    /// <summary>是否成功。</summary>
    public bool Success { get; set; }

    /// <summary>响应码，见 <see cref="BaseApiResponseCode"/>。</summary>
    public int Code { get; set; }

    /// <summary>提示消息，面向用户的中文完整句。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>字段级错误集合。前端只用它做 tip 提示，不飘红输入框（CODING_STANDARD 3.4）。</summary>
    public IDictionary<string, string[]> Errors { get; set; } = new Dictionary<string, string[]>();
}

/// <summary>无数据响应构造重载。</summary>
public static class ApiResponseFactory
{
    /// <summary>构造成功响应。</summary>
    /// <param name="message">可选提示消息。</param>
    public static ApiResponse Ok(string message = "") => new() { Success = true, Code = 0, Message = message };

    /// <summary>构造失败响应。</summary>
    /// <param name="code">响应码。</param>
    /// <param name="message">失败原因。</param>
    /// <param name="errors">字段级错误集合。</param>
    public static ApiResponse Fail(BaseApiResponseCode code, string message, IDictionary<string, string[]>? errors = null)
        => new() { Success = false, Code = (int)code, Message = message, Errors = errors ?? new Dictionary<string, string[]>() };
}