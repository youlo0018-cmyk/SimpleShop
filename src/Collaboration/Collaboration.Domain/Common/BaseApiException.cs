namespace Collaboration.Domain.Common;

/// <summary>
/// 业务异常：带响应码，由全局异常中间件统一翻译成 HTTP 响应。
/// </summary>
/// <remarks>
/// 用于「拒绝执行」类语义（无权限、状态不允许、对象已存在等），
/// 与「意外异常」区分开：业务异常返回明确响应码且不打印 Error 级堆栈。
/// </remarks>
public class BaseApiException : Exception
{
    /// <summary>构造业务异常。</summary>
    /// <param name="code">响应码。</param>
    /// <param name="message">面向用户的中文完整句。</param>
    /// <param name="errors">字段级错误集合，可为 null。</param>
    public BaseApiException(
        BaseApiResponseCode code,
        string message,
        IDictionary<string, string[]>? errors = null)
        : base(message)
    {
        Code = code;
        Errors = errors ?? new Dictionary<string, string[]>();
    }

    /// <summary>响应码。</summary>
    public BaseApiResponseCode Code { get; }

    /// <summary>字段级错误集合。</summary>
    public IDictionary<string, string[]> Errors { get; }
}