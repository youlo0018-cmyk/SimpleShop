namespace MarketingService.Domain.Services;

/// <summary>订单金额端口（金额数据在订单服务）。</summary>
/// <remarks>
/// <b>营销侧只有订单号，金额在订单库</b>，所以「参与金额 / 成交额」必须由订单服务算。
/// 口径（排除待支付 / 已取消 / 已退款）与工作台 GMV 完全一致 ——
/// 营销侧自己按折扣或原价估一遍，报表之间立刻对不上账。
/// </remarks>
public interface IOrderAmountPort
{
    /// <summary>按订单号集合汇总成交额。</summary>
    /// <param name="orderNos">订单号集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已支付（不含已取消 / 已退款）的实付合计；订单服务不可用时返回 0。</returns>
    /// <remarks>
    /// <b>不可用时返回 0 而不是抛异常</b>：金额是报表里的一个数字，
    /// 让它把整张报表拖成 500 代价太大；真实故障由订单服务自己的日志暴露。
    /// </remarks>
    Task<decimal> SumPayableAsync(IReadOnlyCollection<string> orderNos, CancellationToken ct = default);
}
