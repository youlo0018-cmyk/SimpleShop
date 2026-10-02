using Yitter.IdGenerator;

namespace Collaboration.Domain.Infrastructure;

/// <summary>
/// 雪花 Id 生成器的配置与取号入口。
/// </summary>
/// <remarks>
/// 链路位置：服务启动阶段 S4/S5（DATA_SPEC 1.2）先分配 workerId，再调用 Configure，
/// 之后所有插入都由 FreeSql AOP 自动调用 NewId 填主键（DATA_SPEC 3.3）。
/// workerId 来源：Redis INCR 原子自增（DATA_SPEC 3.4），**不用配置写死**。
/// 依据：DATA_SPEC.md 3.3、3.4。
/// </remarks>
public static class SnowflakeId
{
    private static readonly object SyncRoot = new();
    private static bool _configured;

    /// <summary>
    /// 分配的工作节点 Id。
    /// </summary>
    /// <remarks>Configure 时写入，供启动日志与排障使用。</remarks>
    public static ushort WorkerId { get; private set; }

    /// <summary>
    /// 用给定的 workerId 初始化生成器。每个进程只应调用一次。
    /// </summary>
    /// <param name="workerId">工作节点 Id，由 Redis INCR 分配。</param>
    /// <exception cref="InvalidOperationException">重复配置时抛出，避免 workerId 被静默改写导致 Id 重复。</exception>
    public static void Configure(ushort workerId)
    {
        lock (SyncRoot)
        {
            if (_configured)
            {
                throw new InvalidOperationException("雪花生成器已配置，不允许重复配置。");
            }

            WorkerId = workerId;
            YitIdHelper.SetIdGenerator(new IdGeneratorOptions
            {
                WorkerId = workerId,
                WorkerIdBitLength = 6,
                SeqBitLength = 7,
                BaseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                TopOverCostCount = 2000
            });

            _configured = true;
        }
    }

    /// <summary>
    /// 生成一个雪花 Id。
    /// </summary>
    /// <returns>单调递增的 long 型 Id。</returns>
    /// <exception cref="InvalidOperationException">未调用 Configure 时抛出。</exception>
    public static long NewId()
    {
        lock (SyncRoot)
        {
            if (!_configured)
            {
                throw new InvalidOperationException("雪花生成器未配置，请先调用 SnowflakeId.Configure(workerId)。");
            }
        }

        return YitIdHelper.IdGenInstance.NewLong();
    }
}

