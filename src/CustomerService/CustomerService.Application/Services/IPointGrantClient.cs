namespace CustomerService.Application.Services;

/// <summary>积分发放的跨服务契约。注册赠送 100 积分由此发起（BUSINESS 13.2）。</summary>
public interface IPointGrantClient
{
    /// <summary>发放注册赠送积分。</summary>
    /// <param name="customerId">新客户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 表示已发放；false 表示未发放（注册不因此失败，由补偿任务补发）。</returns>
    Task<bool> TryGrantRegistrationBonusAsync(long customerId, CancellationToken ct = default);
}

/// <summary>S1 阶段的占位实现：积分服务在 S6 才建，此处只打通契约。</summary>
/// <remarks>返回 false 而不是 true，是为了让「赠送未发生」在日志与返回值里都可见；
/// 若返回 true 就会伪造出一个并不存在的积分记录。S6 用真实 gRPC 客户端替换本实现。</remarks>
public sealed class UnavailablePointGrantClient : IPointGrantClient
{
    /// <inheritdoc />
    public Task<bool> TryGrantRegistrationBonusAsync(long customerId, CancellationToken ct = default)
    {
        Console.WriteLine($"[point] 积分服务尚未就绪，客户 {customerId} 的注册赠送积分未发放（PLAN.md S6 补发）");
        return Task.FromResult(false);
    }
}

