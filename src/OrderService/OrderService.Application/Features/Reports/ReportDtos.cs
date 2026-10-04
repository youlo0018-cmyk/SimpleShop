namespace OrderService.Application.Features.Reports;

/// <summary>工作台经营报表（BUSINESS.md 17）。</summary>
/// <param name="Range">时间范围档位（1 今日 / 2 昨日 / 3 近 7 天 / 4 近 30 天）。</param>
/// <param name="RangeName">时间范围中文名。后台按它显示，不给前端猜。</param>
/// <param name="From">区间起（含）。</param>
/// <param name="To">区间止（不含）。</param>
/// <param name="Gmv">成交额：已支付实付合计，<b>不含已取消与已退款</b>。</param>
/// <param name="OrderCount">订单数（区间内下单的全部订单，含未支付）。</param>
/// <param name="PaidOrderCount">支付订单数。</param>
/// <param name="CompletedOrderCount">完成订单数。</param>
/// <param name="AvgOrderValue">客单价 = 成交额 / 支付订单数。分母为 0 时返回 0。</param>
/// <param name="RefundAmount">退款金额。</param>
/// <param name="RefundRate">退款率 = 退款金额 / 成交额。分母为 0 时返回 0。</param>
/// <param name="LowStockCount">库存预警 SKU 数。</param>
/// <remarks>
/// 金额全部两位小数、比率以**小数**下发（0.1234）由前端转百分比——
/// 服务端下发百分数会让「0.5%」和「50」两种口径混在一起，前端没法统一格式化。
/// </remarks>
public sealed record BusinessReport(
    int Range,
    string RangeName,
    DateTime From,
    DateTime To,
    decimal Gmv,
    long OrderCount,
    long PaidOrderCount,
    long CompletedOrderCount,
    decimal AvgOrderValue,
    decimal RefundAmount,
    decimal RefundRate,
    int LowStockCount);

/// <summary>订单侧的原始聚合结果。</summary>
/// <param name="OrderCount">下单数。</param>
/// <param name="PaidOrderCount">支付订单数。</param>
/// <param name="CompletedOrderCount">完成订单数。</param>
/// <param name="Gmv">成交额。</param>
public sealed record OrderAggregates(
    long OrderCount, long PaidOrderCount, long CompletedOrderCount, decimal Gmv);

/// <summary>支付侧的原始聚合结果。</summary>
/// <param name="RefundAmount">退款金额合计。</param>
public sealed record RefundAggregate(decimal RefundAmount);
