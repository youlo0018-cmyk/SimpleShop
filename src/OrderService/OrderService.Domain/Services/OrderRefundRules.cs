namespace OrderService.Domain.Services;

/// <summary>订单行的可退余额（供退款规则计算，纯数据）。</summary>
/// <param name="OrderItemId">订单行 Id。</param>
/// <param name="Quantity">下单数量。</param>
/// <param name="RefundedQuantity">已退数量。</param>
/// <param name="PayableAmount">该行实付金额。</param>
/// <param name="RefundedAmount">该行已退金额。</param>
public readonly record struct RefundableLine(
    long OrderItemId,
    int Quantity,
    int RefundedQuantity,
    decimal PayableAmount,
    decimal RefundedAmount)
{
    /// <summary>该行还剩多少件可退。</summary>
    public int RemainingQuantity => Math.Max(0, Quantity - RefundedQuantity);

    /// <summary>该行还剩多少钱可退。</summary>
    public decimal RemainingAmount => OrderRefundRules.Round2(
        Math.Max(0m, PayableAmount - RefundedAmount));
}

/// <summary>退款时调用方指定的一行。</summary>
/// <param name="OrderItemId">订单行 Id。</param>
/// <param name="Quantity">本次退多少件。</param>
/// <param name="Amount">本次退多少钱。</param>
public readonly record struct RefundLineRequest(long OrderItemId, int Quantity, decimal Amount);

/// <summary>本次退款解析出来的一行。</summary>
/// <param name="OrderItemId">订单行 Id；0 表示运费与优惠分摊，不是真实商品行。</param>
/// <param name="Quantity">本次退款数量。</param>
/// <param name="Amount">本次退款金额。</param>
public readonly record struct ResolvedRefundLine(long OrderItemId, int Quantity, decimal Amount);

/// <summary>退款解析结果。</summary>
/// <param name="IsValid">是否解析成功。</param>
/// <param name="Lines">逐行退款明细。</param>
/// <param name="Total">本次退款总额。</param>
/// <param name="RemainingAfter">退款后剩余可退余额。</param>
/// <param name="FullyRefunded">退款后是否已无可退余额。</param>
/// <param name="Error">失败原因；成功时为空串。</param>
public readonly record struct RefundResolution(
    bool IsValid,
    IReadOnlyList<ResolvedRefundLine> Lines,
    decimal Total,
    decimal RemainingAfter,
    bool FullyRefunded,
    string Error);

/// <summary>多次部分退款的纯规则：把「调用方想退什么」解析成「实际能退什么」。</summary>
/// <remarks>
/// <para>放在 Domain 层是为了能被单元测试直接覆盖，因为<b>超退就是资损</b>：
/// 多退的钱要人工追回，而规则只要错一条边界（少一分钱的容差、数量与金额不匹配、
/// 运费怎么摊）就会漏过去。
/// </para>
/// <para>两条关键设计：
/// <list type="bullet">
/// <item><b>行级余额与订单级余额要各自判</b>。行余额是「行实付 − 行已退」，
/// 订单余额是「订单实付 − 订单已退」，后者含运费与积分抵扣，两者本来就对不上。
/// 只判一个必然出错：只判订单余额会让运费被重复退，只判行余额会漏掉运费那一块。</item>
/// <item><b>余额判断留 0.01 容差</b>。两次部分退款各自除不尽时，末次很可能差一分钱，
/// 一分钱的误差不该把正当的退款请求挡在门外。</item>
/// </list>
/// </para>
/// </remarks>
public static class OrderRefundRules
{
    /// <summary>金额舍入：两位小数、四舍五入（AwayFromZero）。</summary>
    /// <param name="value">原始金额。</param>
    /// <returns>两位小数的金额。</returns>
    public static decimal Round2(decimal value)
        => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>解析本次退款。</summary>
    /// <param name="lines">订单所有行的可退余额。</param>
    /// <param name="orderPayableAmount">订单实付金额（含运费、已扣积分抵扣）。</param>
    /// <param name="orderRefundedAmount">订单累计已退金额。</param>
    /// <param name="requests">调用方指定的行；留空表示把剩余可退余额一次退完。</param>
    /// <returns>解析结果。</returns>
    public static RefundResolution Resolve(
        IReadOnlyList<RefundableLine> lines,
        decimal orderPayableAmount,
        decimal orderRefundedAmount,
        IReadOnlyList<RefundLineRequest>? requests)
    {
        var remainingOrder = Round2(orderPayableAmount - orderRefundedAmount);
        if (remainingOrder <= 0.01m)
        {
            return Fail("该订单已全额退款，没有可退余额");
        }

        return requests is null || requests.Count == 0
            ? ResolveWhole(lines, remainingOrder)
            : ResolvePartial(lines, remainingOrder, requests);
    }

    /// <summary>整单退：把剩余余额一次退完。</summary>
    /// <param name="lines">订单所有行的可退余额。</param>
    /// <param name="remainingOrder">订单剩余可退余额。</param>
    /// <returns>解析结果。</returns>
    /// <remarks>
    /// <para><b>行金额要按比例摊到订单余额上，而不是直接拿行金额去比。</b>
    /// 行实付之和是「商品总额」，订单实付是「商品总额 + 运费 − 券 − 积分抵扣」。
    /// 用了券或积分的订单，行金额之和**必然大于**订单实付 —— 这是正常的，
    /// 不是数据异常。之前直接判「行和 &gt; 订单余额就是数据异常」，
    /// 结果是<b>任何用了券或积分的订单都退不了款</b>，而测试用的单子都没用券，
    /// 于是这条路径一次都没被走到过。</para>
    ///
    /// <para>正确做法：按各行金额占比把订单剩余余额摊下去，逐行四舍五入，
    /// 最后一行吸收舍入余数，保证各行之和**恰好等于**订单余额 —— 否则退款总额会差几分钱，
    /// 订单永远退不干净。</para>
    /// </remarks>
    private static RefundResolution ResolveWhole(
        IReadOnlyList<RefundableLine> lines, decimal remainingOrder)
    {
        // 只收「还剩钱」的行：钱已经退完但件数没退完的行不能再回补库存，
        // 否则会出现「钱退完了、货也放回仓库」的双重损失。
        var refundable = lines
            // 只收「还剩钱」的行：钱已经退完但件数没退完的行不能再回补库存，
            // 否则会出现「钱退完了、货也放回仓库」的双重损失。
            .Where(a => a.RemainingAmount > 0.005m)
            .ToList();

        if (refundable.Count == 0)
        {
            // 所有行的钱都退完了，剩下的（运费 / 零头）全部记到分摊行
            return remainderOnly(remainingOrder);
        }

        var lineTotal = Round2(refundable.Sum(a => a.RemainingAmount));
        if (lineTotal <= 0m)
        {
            return remainderOnly(remainingOrder);
        }

        // 摊给商品行的部分 = min(订单余额, 行金额之和)。
        // 两边取小的意义：订单有券 / 积分时余额 < 行和，全摊到行上；
        // 有运费时余额 > 行和，行只摊自己那份，差额留给下面的运费行。
        var lineShare = Round2(Math.Min(remainingOrder, lineTotal));

        // 按行金额占比摊 lineShare。逐行四舍五入后可能差几分钱，
        // 最后一**有金额**的行吸收余数，保证合计正好等于 lineShare。
        var picked = new List<ResolvedRefundLine>();
        var allocated = 0m;
        var lastWithAmount = refundable.Count - 1;

        for (var i = 0; i < refundable.Count; i++)
        {
            var line = refundable[i];
            var amount = i == lastWithAmount
                ? Round2(lineShare - allocated)
                : Round2(Round2(lineShare * line.RemainingAmount / lineTotal));

            allocated = Round2(allocated + amount);
            picked.Add(new ResolvedRefundLine(
                line.OrderItemId, line.RemainingQuantity, amount));
        }

        // 行摊完还剩下的：运费 / 零头。记到约定的分摊行，退款记录里才看得出运费退了多少
        var diff = Round2(remainingOrder - allocated);
        if (diff > 0.005m)
        {
            // OrderItemId = 0 是约定的「运费与优惠分摊」伪行，不对应任何真实商品
            picked.Add(new ResolvedRefundLine(0, 0, diff));
        }
        else if (diff < -0.01m)
        {
            // 兜底：摊完之后**多**了（只可能是舍入组合异常），宁可报错也不能退超
            return Fail($"退款分摊后金额不平（多 {Math.Abs(diff):0.00} 元），请联系技术支持核查");
        }

        return Build(picked, remainingOrder);
    }

    /// <summary>整单退但商品行都已退完：剩余金额全部记到分摊行。</summary>
    /// <param name="remainingOrder">订单剩余可退余额。</param>
    /// <returns>解析结果。</returns>
    private static RefundResolution remainderOnly(decimal remainingOrder)
    {
        if (remainingOrder <= 0.005m)
        {
            return Fail("该订单已无可退余额");
        }

        // OrderItemId = 0 是约定的「运费与优惠分摊」伪行，不对应任何真实商品。
        return Build([new ResolvedRefundLine(0, 0, Round2(remainingOrder))], remainingOrder);
    }

    /// <summary>部分退：逐行校验行级余额。</summary>
    /// <param name="lines">订单所有行的可退余额。</param>
    /// <param name="remainingOrder">订单剩余可退余额。</param>
    /// <param name="requests">调用方指定的行。</param>
    /// <returns>解析结果。</returns>
    private static RefundResolution ResolvePartial(
        IReadOnlyList<RefundableLine> lines,
        decimal remainingOrder,
        IReadOnlyList<RefundLineRequest> requests)
    {
        var picked = new List<ResolvedRefundLine>();

        foreach (var request in requests)
        {
            var line = lines.FirstOrDefault(a => a.OrderItemId == request.OrderItemId);
            if (line.OrderItemId == 0)
            {
                return Fail($"订单行 {request.OrderItemId} 不属于该订单");
            }

            var amount = Round2(request.Amount);
            if (amount <= 0m)
            {
                return Fail("退款金额必须大于 0");
            }

            // 数量上限必须判：退 3 件却只按 1 件的钱退，会让库存回补 3 件而钱只退 1 件的钱。
            if (request.Quantity <= 0 || request.Quantity > line.RemainingQuantity)
            {
                return Fail(
                    $"该商品行最多还能退 {line.RemainingQuantity} 件" +
                    $"（共 {line.Quantity} 件，已退 {line.RefundedQuantity} 件）");
            }

            if (amount > line.RemainingAmount + 0.01m)
            {
                return Fail(
                    $"该商品行最多还能退 {line.RemainingAmount:0.00} 元，本次申请 {amount:0.00} 元");
            }

            picked.Add(new ResolvedRefundLine(line.OrderItemId, request.Quantity, amount));
        }

        return Build(picked, remainingOrder);
    }

    /// <summary>把逐行明细汇总成解析结果。</summary>
    /// <param name="lines">逐行明细。</param>
    /// <param name="remainingOrder">退款前的订单剩余可退余额。</param>
    /// <returns>解析结果。</returns>
    /// <remarks>
    /// 总额这一关只能在汇总之后判：每行的行级余额各自都合法，但它们之和
    /// 仍可能超过订单级余额（运费为 0 且积分抵扣分摊到各行时就会出现）。
    /// </remarks>
    private static RefundResolution Build(IReadOnlyList<ResolvedRefundLine> lines, decimal remainingOrder)
    {
        var total = Round2(lines.Sum(a => a.Amount));
        if (total <= 0m)
        {
            return Fail("请选择要退的商品行并填写退款金额");
        }

        if (total > remainingOrder + 0.01m)
        {
            return Fail($"本次退款 {total:0.00} 元超过订单剩余可退 {remainingOrder:0.00} 元");
        }

        var remainingAfter = Round2(remainingOrder - total);
        return new RefundResolution(
            true, lines, total, remainingAfter, remainingAfter <= 0.01m, string.Empty);
    }

    /// <summary>构造一个失败的解析结果。</summary>
    /// <param name="error">失败原因。</param>
    /// <returns>失败的解析结果。</returns>
    private static RefundResolution Fail(string error)
        => new(false, [], 0m, 0m, false, error);
}
