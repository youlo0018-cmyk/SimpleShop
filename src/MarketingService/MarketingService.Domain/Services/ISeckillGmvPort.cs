namespace MarketingService.Domain.Services;

/// <summary>秒杀 GMV 端口（数据在订单服务）。</summary>
public interface ISeckillGmvPort
{
    /// <summary>按订单号集合汇总成交额。</summary>
    /// <param name="orderNos">订单号集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已支付（不含已取消 / 已退款）的实付合计；订单服务不可用时返回 0。</returns>
    /// <remarks>
    /// <b>秒杀订单在营销服务只有订单号，金额在订单库</b>，所以 GMV 必须由订单服务算。
    /// 口径（排除待支付 / 已取消 / 已退款）与工作台 GMV 完全一致。
    ///
    /// <para><b>不可用时返回 0 而不是抛异常</b>：GMV 是报表里的一个数字，
    /// 让它把整张秒杀报表拖成 500 代价太大；真实故障由订单服务自己的日志暴露。</para>
    /// </remarks>
    Task<decimal> SumPayableAsync(IReadOnlyCollection<string> orderNos, CancellationToken ct = default);
}
