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

