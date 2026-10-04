using Collaboration.Domain.Common;
using LogService.Domain;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogService.Application;

/// <summary>页面访问日志查询处理器。</summary>
public sealed class QueryPvLogsHandler : IRequestHandler<QueryPvLogsCommand, ApiResponse<LogPage>>
{
    private readonly ILogQuery _query;

    /// <summary>构造处理器。</summary>
    /// <param name="query">日志查询端口。</param>
    public QueryPvLogsHandler(ILogQuery query) => _query = query;

    /// <inheritdoc />
    public async Task<ApiResponse<LogPage>> Handle(QueryPvLogsCommand request, CancellationToken ct)
        => ApiResults.Ok(await _query
            .PagePvAsync(LogConditionFactory.Of(
                request.Keyword, request.RequestId, request.Path, request.Method, request.Service,
                request.MinStatusCode, request.From, request.To, request.Page, request.PageSize), ct)
            .ConfigureAwait(false));
}

/// <summary>写操作日志查询处理器。</summary>
public sealed class QueryOperationLogsHandler
    : IRequestHandler<QueryOperationLogsCommand, ApiResponse<LogPage>>
{
    private readonly ILogQuery _query;

    /// <summary>构造处理器。</summary>
    /// <param name="query">日志查询端口。</param>
    public QueryOperationLogsHandler(ILogQuery query) => _query = query;

    /// <inheritdoc />
    public async Task<ApiResponse<LogPage>> Handle(
        QueryOperationLogsCommand request, CancellationToken ct)
        => ApiResults.Ok(await _query.PageOperationAsync(LogConditionFactory.Of(
                request.Keyword, request.RequestId, request.Path, request.Method, request.Service,
                request.MinStatusCode, request.From, request.To, request.Page, request.PageSize), ct)
            .ConfigureAwait(false));
}

/// <summary>异常日志查询处理器。</summary>
public sealed class QueryExceptionLogsHandler
    : IRequestHandler<QueryExceptionLogsCommand, ApiResponse<LogPage>>
{
    private readonly ILogQuery _query;

    /// <summary>构造处理器。</summary>
    /// <param name="query">日志查询端口。</param>
    public QueryExceptionLogsHandler(ILogQuery query) => _query = query;

    /// <inheritdoc />
    public async Task<ApiResponse<LogPage>> Handle(
        QueryExceptionLogsCommand request, CancellationToken ct)
        => ApiResults.Ok(await _query.PageExceptionAsync(LogConditionFactory.Of(
                request.Keyword, request.RequestId, request.Path, request.Method, request.Service,
                request.MinStatusCode, request.From, request.To, request.Page, request.PageSize), ct)
            .ConfigureAwait(false));
}

/// <summary>异常调用栈查询处理器。</summary>
public sealed class GetExceptionStackHandler : IRequestHandler<GetExceptionStackCommand, ApiResponse<string>>
{
    private readonly ILogQuery _query;

    /// <summary>构造处理器。</summary>
    /// <param name="query">日志查询端口。</param>
    public GetExceptionStackHandler(ILogQuery query) => _query = query;

    /// <inheritdoc />
    public async Task<ApiResponse<string>> Handle(GetExceptionStackCommand request, CancellationToken ct)
    {
        var stack = await _query.GetExceptionStackAsync(request.Id, ct).ConfigureAwait(false);
        return ApiResults.Ok(stack);
    }
}

/// <summary>死信列表查询处理器。</summary>
public sealed class QueryDeadLettersHandler
    : IRequestHandler<QueryDeadLettersCommand, ApiResponse<DeadLetterPage>>
{
    private readonly IDeadLetterRepository _repo;

    /// <summary>构造处理器。</summary>
    /// <param name="repo">死信仓储。</param>
    public QueryDeadLettersHandler(IDeadLetterRepository repo) => _repo = repo;

    /// <inheritdoc />
    public async Task<ApiResponse<DeadLetterPage>> Handle(
        QueryDeadLettersCommand request, CancellationToken ct)
    {
        var page = await _repo
            .PageAsync(request.EventType ?? string.Empty, request.Page, request.PageSize, ct)
            .ConfigureAwait(false);

        return ApiResults.Ok(page);
    }
}

/// <summary>死信重放处理器。</summary>
public sealed class ReplayDeadLetterHandler : IRequestHandler<ReplayDeadLetterCommand, ApiResponse>
{
    private readonly IDeadLetterRepository _repo;
    private readonly IDeadLetterReplayer _replayer;
    private readonly int _maxReplayCount;
    private readonly ILogger<ReplayDeadLetterHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="repo">死信仓储。</param>
    /// <param name="replayer">重放端口。</param>
    /// <param name="options">重放配置。</param>
    /// <param name="logger">日志器。</param>
    /// <remarks>
    /// 注入的是 <see cref="IOptions{TOptions}"/> 而不是裸的 Options 类型：
    /// <c>services.Configure&lt;T&gt;(section)</c> 注册的是 <c>IOptions&lt;T&gt;</c>，
    /// 直接注入 T 会在启动时被 DI 校验拦下（这个拦截是好事，
    /// 比等到用户点「重放」才 500 强）。
    /// </remarks>
    public ReplayDeadLetterHandler(
        IDeadLetterRepository repo,
        IDeadLetterReplayer replayer,
        IOptions<DeadLetterReplayOptions> options,
        ILogger<ReplayDeadLetterHandler> logger)
    {
        _repo = repo;
        _replayer = replayer;
        _maxReplayCount = options.Value.MaxReplayCount;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 顺序不能换：<b>先确认能从队列里捞到消息，再更新重放次数</b>。
    /// 反过来的话，队列里已经没有这条消息时计数会被白白 +1，
    /// 用户会看到「明明没重放成功，次数却涨了」，而重放次数到顶后就再也放不了了。
    /// </remarks>
    public async Task<ApiResponse> Handle(ReplayDeadLetterCommand request, CancellationToken ct)
    {
        var record = await _repo.GetAsync(request.EventId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "死信记录不存在");
        }

        var max = DeadLetterRules.EffectiveMax(_maxReplayCount);
        if (!DeadLetterRules.CanReplay(record, max))
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BadRequest,
                $"该消息已重放 {record.ReplayCount} 次，达到上限 {max} 次，不再重复重放");
        }

        bool replayed;
        try
        {
            replayed = await _replayer.ReplayAsync(request.EventId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重放死信 {EventId} 失败", request.EventId);
            return ApiResponseFactory.Fail(BaseApiResponseCode.InternalError, "重放失败，请稍后重试");
        }

        if (!replayed)
        {
            // 记录还在但队列里已经没有了：可能已被人工在 RabbitMQ 管理台捞走。
            // 这时**不**加计数，而是让操作人去核对，而不是默默成功。
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.NotFound, "死信队列里已找不到该消息，可能已被人工处理");
        }

        await _repo.MarkReplayedAsync(request.EventId, DateTime.UtcNow, ct).ConfigureAwait(false);
        return ApiResponseFactory.Ok("重放成功");
    }
}

/// <summary>死信重放配置。</summary>
public sealed class DeadLetterReplayOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "DeadLetter";

    /// <summary>单条消息最多重放几次。</summary>
    public int MaxReplayCount { get; set; } = DeadLetterRules.DefaultMaxReplayCount;
}

/// <summary>把三个查询命令共有的十个字段收敛成一处。</summary>
/// <remarks>
/// 三个命令字段完全一样却互相不继承，是刻意的：让它们实现同一个接口会让
/// 「页面访问日志请求里带上 operatorId」这种字段悄悄混进来。
/// 代价是这里要把字段一个个列出来——比继承带来的耦合便宜。
/// </remarks>
internal static class LogConditionFactory
{
    /// <summary>构造查询条件。</summary>
    /// <param name="keyword">关键字。</param>
    /// <param name="requestId">请求 Id。</param>
    /// <param name="path">请求路径。</param>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="service">服务名。</param>
    /// <param name="minStatusCode">响应码下界。</param>
    /// <param name="from">开始时间。</param>
    /// <param name="to">结束时间。</param>
    /// <param name="page">页码。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <returns>查询条件。</returns>
    internal static LogQueryCondition Of(
        string? keyword, string? requestId, string? path, string? method, string? service,
        int minStatusCode, DateTime? from, DateTime? to, int page, int pageSize)
        => new(keyword, requestId, path, method, service, minStatusCode, from, to, page, pageSize);
}
