namespace Collaboration.Domain.Messaging;

/// <summary>页面访问日志事件载荷（pv.log）。</summary>
/// <remarks>
/// <b>它住在 Collaboration 而不是 LogService</b>：这是跨服务的**事件契约**，
/// 生产方是网关与各业务服务、消费方是 LogService，两边都得认识它。
/// 放在 LogService.Domain 里的话，生产方就得反过来引用日志服务，
/// 一个只写日志的服务成了所有服务的编译期依赖。
/// </remarks>
/// <param name="OccurredAt">发生时间 UTC。</param>
/// <param name="Service">产生日志的服务名。</param>
/// <param name="Path">请求路径。</param>
/// <param name="QueryString">查询串，不含问号。</param>
/// <param name="Method">HTTP 方法。</param>
/// <param name="StatusCode">响应码。</param>
/// <param name="ElapsedMs">耗时毫秒。</param>
/// <param name="ClientIp">客户端 IP。</param>
/// <param name="UserAgent">User-Agent。</param>
/// <param name="RequestId">请求 Id，用于串联同一次请求的多条日志。</param>
public sealed record PvLogEntry(
    DateTime OccurredAt,
    string Service,
    string Path,
    string QueryString,
    string Method,
    int StatusCode,
    long ElapsedMs,
    string ClientIp,
    string UserAgent,
    string RequestId);

/// <summary>写操作日志事件载荷（operation.log）。</summary>
/// <remarks>只对写方法（POST / PUT / PATCH / DELETE）发，读操作进 pv.log 就够了。</remarks>
/// <param name="OccurredAt">发生时间 UTC。</param>
/// <param name="Service">服务名。</param>
/// <param name="OperatorId">操作人 Id，0 表示匿名。</param>
/// <param name="OperatorName">操作人姓名。</param>
/// <param name="IsAdmin">true 后台操作 / false 小程序操作。</param>
/// <param name="Method">HTTP 方法。</param>
/// <param name="Path">请求路径。</param>
/// <param name="StatusCode">响应码。</param>
/// <param name="RequestId">请求 Id。</param>
/// <param name="ElapsedMs">耗时毫秒。</param>
public sealed record OperationLogEntry(
    DateTime OccurredAt,
    string Service,
    long OperatorId,
    string OperatorName,
    bool IsAdmin,
    string Method,
    string Path,
    int StatusCode,
    string RequestId,
    long ElapsedMs);

/// <summary>未处理异常事件载荷（exception.log）。</summary>
/// <param name="OccurredAt">发生时间 UTC。</param>
/// <param name="Service">服务名。</param>
/// <param name="Path">请求路径。</param>
/// <param name="Method">HTTP 方法。</param>
/// <param name="ExceptionType">异常类型全名。</param>
/// <param name="Message">异常消息。</param>
/// <param name="StackTrace">调用栈。</param>
/// <param name="RequestId">请求 Id。</param>
public sealed record ExceptionLogEntry(
    DateTime OccurredAt,
    string Service,
    string Path,
    string Method,
    string ExceptionType,
    string Message,
    string StackTrace,
    string RequestId);
