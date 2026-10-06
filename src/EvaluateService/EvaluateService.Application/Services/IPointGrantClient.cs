namespace EvaluateService.Application.Services;

/// <summary>积分发放的跨服务契约。发表首评赠送 20 积分由此发起（BUSINESS.md 13.2）。</summary>
/// <remarks>
/// <b>发放失败不让评价失败</b>：评价已经落库了，为了 20 积分把整条评价回滚，
/// 用户看到的是「评价失败」却不知道为什么。返回 false 让评价照常成功，
/// 由补偿任务补发 —— 与注册赠送、订单发积分是同一套取舍。
/// </remarks>
public interface IPointGrantClient
{
    /// <summary>发放发表首评的赠送积分。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <param name="evaluateId">评价 Id，用作幂等键的一部分。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 表示已发放；false 表示未发放（评价不因此失败）。</returns>
    Task<bool> TryGrantEvaluateBonusAsync(long customerId, long evaluateId, CancellationToken ct = default);
}

