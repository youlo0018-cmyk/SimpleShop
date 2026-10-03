namespace ScheduledService.Jobs;

/// <summary>一个定时任务。</summary>
/// <remarks>
/// <para>定时任务<b>只回答「什么时候做」，不回答「做什么」</b>：
/// 具体做什么由被调用的服务自己判断。这是本项目的一条硬边界——
/// 定时任务不查任何业务库、不写任何业务表，只调别人暴露的接口。</para>
///
/// <para>所以实现类里出现 <c>ISelect&lt;</c>、<c>Insert</c> 这类东西就说明设计错了：
/// 那意味着它在替别的服务做业务决策，而那些规则会被复制一份、改的时候只改一处。</para>
/// </remarks>
public interface IJob
{
    /// <summary>任务名。用于日志、Redis 互斥键 <c>lock:job:{名字}</c> 与开关配置。</summary>
    string Name { get; }

    /// <summary>执行间隔（秒）。</summary>
    int IntervalSeconds { get; }

    /// <summary>互斥锁的持有上限（秒）。</summary>
    /// <remarks>
    /// 必须**大于**执行间隔的常见耗时，否则任务还没跑完锁就过期了，
    /// 另一个实例会拿到锁并进来重复执行——这正是互斥要防的事。
    /// </remarks>
    int LockTtlSeconds { get; }

    /// <summary>
    /// 首次执行前额外等待的秒数，默认 0。
    /// </summary>
    /// <remarks>
    /// 存在的理由：多个任务都是整点启动时，光靠「随机等 200~1200 毫秒」只能避免
    /// 请求在同一秒发出，但两个**都会扫全表**的重任务仍然会在同一秒压数据库，
    /// 连接池被打满后彼此超时、互相拖慢。给重任务设一个明确的初始延迟
    /// （比如评价重算推迟 30 分钟）才是真正的错峰。
    /// </remarks>
    int InitialDelaySeconds => 0;

    /// <summary>执行一次。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>本次执行的摘要，写进日志。</returns>
    Task<JobRunResult> ExecuteAsync(CancellationToken ct);
}

/// <summary>任务执行结果。</summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="Summary">一句话摘要，给日志看。</param>
/// <param name="Error">失败原因，成功时为空。</param>
/// <remarks>
/// 任务失败<b>不抛异常</b>：抛了会让整个循环停下来，
/// 一个下游抖动导致后面所有任务永久不再执行——这是定时任务最常见也最难发现的故障形态。
/// 失败记下来，下一轮继续。
/// </remarks>
public readonly record struct JobRunResult(bool Succeeded, string Summary, string Error = "")
{
    /// <summary>构造成功结果。</summary>
    /// <param name="summary">一句话摘要。</param>
    public static JobRunResult Ok(string summary) => new(true, summary);

    /// <summary>构造失败结果。</summary>
    /// <param name="summary">一句话摘要。</param>
    /// <param name="error">失败原因。</param>
    public static JobRunResult Fail(string summary, string error) => new(false, summary, error);
}
