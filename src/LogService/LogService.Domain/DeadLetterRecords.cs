namespace LogService.Domain;

/// <summary>死信记录（一条消息重试耗尽后留下的痕迹）。</summary>
/// <remarks>
/// 与 ES 里的 pv / operation / exception 索引分开存：死信是<b>待处理的待办</b>，
/// 混进日志索引里既查不出来，也没法做「已重放」的标记。
/// </remarks>
/// <param name="EventId">事件 Id。重放时按它去死信队列里捞原始消息。</param>
/// <param name="EventType">事件类型（routing key）。</param>
/// <param name="OccurredAt">事件发生时间 UTC。</param>
/// <param name="QueueName">消费队列名。</param>
/// <param name="ErrorMessage">失败原因。</param>
/// <param name="ErrorType">异常类型全名。</param>
/// <param name="StackTrace">调用栈。</param>
/// <param name="FailedAt">判定失败时间 UTC。</param>
/// <param name="Attempts">已尝试次数。</param>
/// <param name="PayloadPreview">原始消息体预览。</param>
/// <param name="ReplayCount">已重放次数。达到上限后拒绝再放。</param>
/// <param name="LastReplayAt">最近一次重放时间 UTC，从未重放为 null。</param>
public sealed record DeadLetterRecord(
    string EventId,
    string EventType,
    DateTime OccurredAt,
    string QueueName,
    string ErrorMessage,
    string ErrorType,
    string StackTrace,
    DateTime FailedAt,
    int Attempts,
    string PayloadPreview,
    int ReplayCount = 0,
    DateTime? LastReplayAt = null);

/// <summary>死信查询与重放的纯规则。</summary>
public static class DeadLetterRules
{
    /// <summary>默认重放上限。</summary>
    /// <remarks>
    /// 为什么要有上限：一条因为「下游没配好」而失败的消息，重放一万次也是同样地失败。
    /// 无上限的重放等于拿 MQ 做压力测试，把刚恢复的下游再打挂一次。
    /// </remarks>
    public const int DefaultMaxReplayCount = 3;

    /// <summary>判断一条死信还能不能重放。</summary>
    /// <param name="record">死信记录。</param>
    /// <param name="maxReplayCount">重放上限，小于等于 0 时取 <see cref="DefaultMaxReplayCount"/>。</param>
    /// <returns>可以重放返回 true。</returns>
    public static bool CanReplay(DeadLetterRecord record, int maxReplayCount)
        => record.ReplayCount < EffectiveMax(maxReplayCount);

    /// <summary>取生效的重放上限。</summary>
    /// <param name="maxReplayCount">配置的重放上限。</param>
    /// <returns>生效的上限，至少为 1。</returns>
    /// <remarks>
    /// 配成 0 的人想的是「别重放」，但那样死信就永远出不来、只能手工删。
    /// 所以这里兜底成 1：至少允许人工确认后重放一次。
    /// </remarks>
    public static int EffectiveMax(int maxReplayCount)
        => maxReplayCount <= 0 ? DefaultMaxReplayCount : maxReplayCount;
}
