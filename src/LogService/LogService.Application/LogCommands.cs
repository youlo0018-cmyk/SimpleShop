using Collaboration.Domain.Common;
using LogService.Domain;
using MediatR;

namespace LogService.Application;

/// <summary>查页面访问日志（后台）。</summary>
/// <param name="Keyword">关键字。</param>
/// <param name="RequestId">请求 Id。</param>
/// <param name="Path">请求路径。</param>
/// <param name="Method">HTTP 方法。</param>
/// <param name="Service">服务名。</param>
/// <param name="MinStatusCode">响应码下界。</param>
/// <param name="From">开始时间 UTC。</param>
/// <param name="To">结束时间 UTC。</param>
/// <param name="Page">页码。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryPvLogsCommand(
    string? Keyword = null, string? RequestId = null, string? Path = null, string? Method = null,
    string? Service = null,
    int MinStatusCode = 0, DateTime? From = null, DateTime? To = null,
    int Page = 1, int PageSize = 20) : IRequest<ApiResponse<LogPage>>, ILogQueryFields;

/// <summary>查写操作日志（后台）。</summary>
/// <param name="Keyword">关键字。</param>
/// <param name="RequestId">请求 Id。</param>
/// <param name="Path">请求路径。</param>
/// <param name="Method">HTTP 方法。</param>
/// <param name="Service">服务名。</param>
/// <param name="MinStatusCode">响应码下界。</param>
/// <param name="From">开始时间 UTC。</param>
/// <param name="To">结束时间 UTC。</param>
/// <param name="Page">页码。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryOperationLogsCommand(
    string? Keyword = null, string? RequestId = null, string? Path = null, string? Method = null,
    string? Service = null,
    int MinStatusCode = 0, DateTime? From = null, DateTime? To = null,
    int Page = 1, int PageSize = 20) : IRequest<ApiResponse<LogPage>>, ILogQueryFields;

/// <summary>查异常日志（后台）。</summary>
/// <param name="Keyword">关键字。</param>
/// <param name="RequestId">请求 Id。</param>
/// <param name="Path">请求路径。</param>
/// <param name="Method">HTTP 方法。</param>
/// <param name="Service">服务名。</param>
/// <param name="MinStatusCode">响应码下界。</param>
/// <param name="From">开始时间 UTC。</param>
/// <param name="To">结束时间 UTC。</param>
/// <param name="Page">页码。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryExceptionLogsCommand(
    string? Keyword = null, string? RequestId = null, string? Path = null, string? Method = null,
    string? Service = null,
    int MinStatusCode = 0, DateTime? From = null, DateTime? To = null,
    int Page = 1, int PageSize = 20) : IRequest<ApiResponse<LogPage>>, ILogQueryFields;

/// <summary>查某条异常的完整调用栈。</summary>
/// <remarks>
/// 列表里不带完整堆栈：一条异常的堆栈动辄几 KB，一页 20 条就是上百 KB 纯噪音。
/// 后台的做法是列表点开再拉详情。
/// </remarks>
/// <param name="Id">ES 文档 Id。</param>
public record GetExceptionStackCommand(string Id) : IRequest<ApiResponse<string>>;

/// <summary>查死信列表（后台）。</summary>
/// <param name="EventType">事件类型过滤，空表示全部。</param>
/// <param name="Page">页码。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryDeadLettersCommand(
    string? EventType = null, int Page = 1, int PageSize = 20)
    : IRequest<ApiResponse<DeadLetterPage>>;

/// <summary>重放一条死信。</summary>
/// <param name="EventId">事件 Id。</param>
public record ReplayDeadLetterCommand(string EventId) : IRequest<ApiResponse>;
