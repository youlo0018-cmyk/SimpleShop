namespace Collaboration.Domain.Common;

/// <summary>
/// 统一响应构造器。所有 Handler 的成功与失败返回都走这里。
/// </summary>
/// <remarks>
/// 存在的理由：保证前端收到的 code / message / errors 结构一致，避免每个 Handler 各写各的。
/// </remarks>
public static class ApiResults
{
    /// <summary>
    /// 构造成功响应。
    /// </summary>
    /// <typeparam name="T">业务数据类型。</typeparam>
    /// <param name="data">业务数据，可为 null。</param>
    /// <param name="message">可选提示消息，成功时通常留空。</param>
    /// <returns>Success 为 true、Code 为 0 的响应体。</returns>
    public static ApiResponse<T> Ok<T>(T? data = default, string message = "")
        => new() { Success = true, Code = (int)BaseApiResponseCode.Success, Message = message, Data = data };

    /// <summary>
    /// 构造无数据的成功响应。
    /// </summary>
    /// <returns>Success 为 true、Data 为 null 的响应体。</returns>
    public static ApiResponse<object> Ok() => Ok<object>(null);

    /// <summary>
    /// 构造失败响应。
    /// </summary>
    /// <typeparam name="T">业务数据类型，失败时 Data 恒为 null。</typeparam>
    /// <param name="code">响应码，见 <see cref="BaseApiResponseCode"/>。</param>
    /// <param name="message">失败原因，面向用户的中文完整句。</param>
    /// <param name="errors">字段级错误集合，可为 null。</param>
    /// <returns>Success 为 false 的响应体。</returns>
    public static ApiResponse<T> Fail<T>(
        BaseApiResponseCode code,
        string message,
        IDictionary<string, string[]>? errors = null)
        => new()
        {
            Success = false,
            Code = (int)code,
            Message = message,
            Data = default,
            Errors = errors ?? new Dictionary<string, string[]>()
        };

    /// <summary>
    /// 构造无数据的失败响应。
    /// </summary>
    /// <param name="code">响应码。</param>
    /// <param name="message">失败原因。</param>
    /// <param name="errors">字段级错误集合，可为 null。</param>
    /// <returns>Success 为 false、Data 为 null 的响应体。</returns>
    public static ApiResponse<object> Fail(
        BaseApiResponseCode code,
        string message,
        IDictionary<string, string[]>? errors = null)
        => Fail<object>(code, message, errors);
}

