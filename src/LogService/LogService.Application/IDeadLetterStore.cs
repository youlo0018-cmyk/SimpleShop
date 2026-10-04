using LogService.Domain;

namespace LogService.Application;

/// <summary>死信记录仓储端口（失败原因落库 + 后台查询）。</summary>
public interface IDeadLetterRepository
{
    /// <summary>写入或更新一条死信记录。</summary>
    /// <param name="record">死信记录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 按 <see cref="DeadLetterRecord.EventId"/> <b>幂等覆盖</b>：
    /// 同一条消息重投失败会写多次，覆盖成「最后一次的失败原因 + 累计次数」，
    /// 而不是攒出 N 条重复记录。
    /// </remarks>
    Task RecordAsync(DeadLetterRecord record, CancellationToken ct = default);

    /// <summary>分页查死信，按失败时间倒序。</summary>
    /// <param name="eventType">按事件类型过滤，空表示不过滤。</param>
    /// <param name="page">页码，从 1 起。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>死信分页结果。</returns>
    Task<DeadLetterPage> PageAsync(
        string eventType, int page, int pageSize, CancellationToken ct = default);

    /// <summary>按事件 Id 取一条死信。</summary>
    /// <param name="eventId">事件 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>找到返回记录，否则返回 null。</returns>
    Task<DeadLetterRecord?> GetAsync(string eventId, CancellationToken ct = default);

    /// <summary>标记一条死信已重放，重放次数 +1。</summary>
    /// <param name="eventId">事件 Id。</param>
    /// <param name="replayedAt">重放时间 UTC。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>标记成功返回 true；记录不存在返回 false。</returns>
    /// <remarks>
    /// 用「读-改-写」而不是 ES 的 update 脚本也可以，因为死信重放是人工触发的低频操作，
    /// 两人同时点同一条的概率可以忽略。写成条件更新反而更复杂、还容易写错。
    /// </remarks>
    Task<bool> MarkReplayedAsync(string eventId, DateTime replayedAt, CancellationToken ct = default);
}

/// <summary>死信重放端口：从 RabbitMQ 死信队列捞回原始消息并重新投递。</summary>
public interface IDeadLetterReplayer
{
    /// <summary>从死信队列按事件 Id 取出原始消息并重新投递到业务交换机。</summary>
    /// <param name="eventId">事件 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>重放成功返回 true；队列里找不到该消息返回 false。</returns>
    /// <remarks>
    /// <b>必须按 EventId 精确捞，不能无脑 requeue 整条队列</b>：
    /// 死信队列里可能同时躺着支付成功、下单这类关键事件，
    /// 全量重放等于把一批旧业务事件重新打一遍，副作用是重复扣库存、重复发积分。
    /// </remarks>
    Task<bool> ReplayAsync(string eventId, CancellationToken ct = default);
}

/// <summary>死信分页结果。</summary>
/// <param name="Items">当前页死信。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record DeadLetterPage(
    IReadOnlyList<DeadLetterRecord> Items, long Total, int Page, int PageSize);
