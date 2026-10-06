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

    /// <summary>按订单号集合取**逐单**实付金额。</summary>
    /// <param name="orderNos">订单号集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>订单号 → 实付金额；订单服务不可用或查不到时缺项（调用方按 0 计）。</returns>
    /// <remarks>
    /// 秒杀报表要按**场次**算 GMV，而「订单号属于哪个场次」只有营销服务知道。
    /// 只回总额的话，营销侧要么每个场次调一次（N 次跨服务调用），
    /// 要么只能给一个全场总额 —— 后者就是「售罄率能逐场比、GMV 却只有一行」的原因。
    /// </remarks>
    Task<IReadOnlyDictionary<string, decimal>> GetPayableByOrderAsync(
        IReadOnlyCollection<string> orderNos, CancellationToken ct = default);
}
