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
/// <param name="Metrics">
/// 指标卡片：每项带 <c>Label</c> 与已格式化的 <c>Value</c>，工作台直接渲染，**前端不写死文案**
/// （DATA_SPEC 4.3）。与上面的同名字段是**同一份数据**：扁平字段给报表页做二次计算，
/// Metrics 给工作台的指标卡直接用。
/// </param>
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
    int LowStockCount,
    IReadOnlyList<ReportMetric> Metrics);

/// <summary>报表指标卡。</summary>
/// <param name="Key">指标键，前端做跳转 / 埋点用（不要用它当文案）。</param>
/// <param name="Label">中文指标名，**后端下发**。</param>
/// <param name="Value">已格式化的展示值（金额两位小数带千分位、比率已转百分比）。</param>
/// <param name="Unit">单位后缀，如「元」「单」；无单位为空串。</param>
/// <remarks>
/// 值在这里就格式化好，是为了避免「前端各写一套格式化」：同一个月度报表在两个页面
/// 各写一遍，迟早出现一处两位小数、一处四位小数。比率由服务端转成百分数**字符串**，
/// 也让 4.6「比率以小数下发」那条规则不会外溢到展示层。
/// </remarks>
public sealed record ReportMetric(string Key, string Label, string Value, string Unit);

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
