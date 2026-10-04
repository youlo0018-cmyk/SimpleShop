namespace LogService.Application;

/// <summary>日志查询条件（三类日志共用）。</summary>
/// <param name="Keyword">模糊关键字，匹配路径 / 消息 / 操作人姓名 / 异常消息。</param>
/// <param name="RequestId">按请求 Id 精确过滤，用于把一次请求的多条日志串起来。</param>
/// <param name="Path">按请求路径精确过滤。</param>
/// <param name="Method">按 HTTP 方法过滤，空表示不过滤。</param>
/// <param name="Service">按服务名过滤，空表示不过滤。后台排查时最常用的维度。</param>
/// <param name="MinStatusCode">响应码下界（含）。0 表示不过滤。</param>
/// <param name="From">开始时间 UTC（含），null 表示不限。</param>
/// <param name="To">结束时间 UTC（含），null 表示不限。</param>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record LogQueryCondition(
    string? Keyword = null,
    string? RequestId = null,
    string? Path = null,
    string? Method = null,
    string? Service = null,
    int MinStatusCode = 0,
    DateTime? From = null,
    DateTime? To = null,
    int Page = 1,
    int PageSize = 20);

/// <summary>一条日志的统一查询视图。</summary>
/// <param name="Id">ES 文档 Id。</param>
/// <param name="OccurredAt">发生时间 UTC。</param>
/// <param name="Service">产生日志的服务名。</param>
/// <param name="Method">HTTP 方法。</param>
/// <param name="Path">请求路径。</param>
/// <param name="StatusCode">响应码。</param>
/// <param name="ElapsedMs">耗时毫秒。</param>
/// <param name="RequestId">请求 Id。</param>
/// <param name="OperatorName">操作人姓名（操作日志有值）。</param>
/// <param name="IsAdmin">是否后台操作。</param>
/// <param name="Message">摘要信息：异常日志是异常消息，其余为空。</param>
public sealed record LogView(
    string Id,
    DateTime OccurredAt,
    string Service,
    string Method,
    string Path,
    int StatusCode,
    long ElapsedMs,
    string RequestId,
    string OperatorName,
    bool IsAdmin,
    string Message);

/// <summary>日志分页结果。</summary>
/// <param name="Items">当前页日志。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record LogPage(
    IReadOnlyList<LogView> Items, long Total, int Page, int PageSize);

/// <summary>日志查询端口（读侧）。</summary>
public interface ILogQuery
{
    /// <summary>查页面访问日志。</summary>
    /// <param name="condition">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页结果。</returns>
    Task<LogPage> PagePvAsync(LogQueryCondition condition, CancellationToken ct = default);

    /// <summary>查写操作日志。</summary>
    /// <param name="condition">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页结果。</returns>
    Task<LogPage> PageOperationAsync(LogQueryCondition condition, CancellationToken ct = default);

    /// <summary>查异常日志。</summary>
    /// <param name="condition">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分页结果。</returns>
    Task<LogPage> PageExceptionAsync(LogQueryCondition condition, CancellationToken ct = default);

    /// <summary>查异常日志详情（含完整调用栈）。</summary>
    /// <param name="id">ES 文档 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>调用栈全文；找不到返回空串。</returns>
    Task<string> GetExceptionStackAsync(string id, CancellationToken ct = default);
}
