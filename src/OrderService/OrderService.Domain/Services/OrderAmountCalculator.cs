namespace OrderService.Domain.Services;

/// <summary>下单的一个订单行（金额计算只需要这些字段）。</summary>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Quantity">数量。</param>
/// <param name="UnitPrice">SKU 售价快照。</param>
public readonly record struct OrderLineInput(long SkuId, int Quantity, decimal UnitPrice);

/// <summary>一行的金额拆分。</summary>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Quantity">数量。</param>
/// <param name="OriginalAmount">原行金额 = 售价 × 数量。</param>
/// <param name="ActivityDiscount">活动优惠额。</param>
/// <param name="CouponDiscount">该行分摊到的券优惠额。</param>
/// <param name="PayableAmount">行应付，已封底。</param>
/// <param name="IsFloored">是否被 0.01 封底抬起来了。</param>
public readonly record struct OrderLineAmount(
    long SkuId, int Quantity, decimal OriginalAmount,
    decimal ActivityDiscount, decimal CouponDiscount,
    decimal PayableAmount, bool IsFloored);

/// <summary>整单金额拆分。</summary>
/// <param name="Lines">逐行拆分。</param>
/// <param name="GoodsTotal">商品总额 = Σ 各行应付（由行累加）。</param>
/// <param name="Freight">运费。</param>
/// <param name="PointsDeduction">积分抵扣金额。</param>
/// <param name="PayableAmount">实付 = 商品总额 + 运费 − 积分抵扣。</param>
public readonly record struct OrderAmount(
    IReadOnlyList<OrderLineAmount> Lines,
    decimal GoodsTotal,
    decimal Freight,
    decimal PointsDeduction,
    decimal PayableAmount);

/// <summary>运费与包邮规则。</summary>
/// <param name="Freight">平台运费，满额包邮时传 0。</param>
/// <param name="FreeShippingThreshold">包邮门槛，0 = 不包邮。</param>
public readonly record struct FreightRule(decimal Freight, decimal FreeShippingThreshold = 0m);

/// <summary>下单金额计算。纯函数，不碰数据库，所以可以单元测试。</summary>
/// <remarks>
/// <para>规则来自 BUSINESS.md 8.4，逐步舍入，<b>每一步都要单独 Round 2</b>，
/// 中间不合成一个表达式——合起来算再舍一次，差一分钱是必然的。</para>
///
/// <para><b>最关键的一条：商品总额由各行应付累加得出</b>（第 5 步），
/// 绝不能反过来用「总金额减总优惠」。后者会出现「各行加起来与订单总额差一分钱」，
/// 而且对账时很难定位。</para>
///
/// <para><b>行封底 0.01</b>：券或活动把行压到 0 时抬到 0.01——
/// 券和活动不能把订单打成 0 元。<b>只有积分抵扣可以</b>把整单打到� 0.00（8.2）。</para>
/// </remarks>
public static class OrderAmountCalculator
{
    /// <summary>
    /// 抵扣汇率的兜底值（多少积分抵 1.00 元）。
    /// </summary>
    /// <remarks>
    /// 只在拿不到积分规则时使用（积分服务不可用、规则没配）。
    /// 正常路径由 <c>IPointPort</c> 从积分服务取真实汇率 —— 见 <c>Calculate</c> 的参数说明。
    /// </remarks>
    public const long DefaultPointsPerYuan = 100;

    /// <summary>全系统统一的舍入口径（BUSINESS.md 8.4）。</summary>
    /// <param name="value">原始金额。</param>
    /// <returns>两位小数、四舍五入。</returns>
    /// <remarks>
    /// 用 AwayFromZero 而不是 .NET 默认的 ToEven：PostgreSQL 的 numeric 转 numeric(18,2)
    /// 走的是银行家舍入（0.125 → 0.12），与中文语境的「四舍五入」不符。
    /// 入库前必须在应用层舍好，列类型只是最后一道存储约束。
    /// </remarks>
    public static decimal Round2(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 计算整单金额。
    /// </summary>
    /// <param name="lines">订单行。</param>
    /// <param name="lineCouponDiscounts">每行分摊到的券优惠额，长度须与 lines 一致。</param>
    /// <param name="lineActivityDiscounts">每行的活动优惠额，长度须与 lines 一致。</param>
    /// <param name="freightRule">运费规则。</param>
    /// <param name="pointsToUse">本次抵扣的积分数（整数）。</param>
    /// <returns>整单金额拆分。</returns>
    /// <remarks>
    /// <paramref name="lineCouponDiscounts"/> 是「按行**已经分摊好**的券优惠」，
    /// 不是整张券的面额——分摊在上游（Marketing 结算）完成，
    /// 这里只负责把它减到行上。混用会导致同一张券的钱被减两次。
    /// </remarks>
    public static OrderAmount Calculate(
        IReadOnlyList<OrderLineInput> lines,
        IReadOnlyList<decimal> lineCouponDiscounts,
        IReadOnlyList<decimal> lineActivityDiscounts,
        FreightRule freightRule = default,
        long pointsToUse = 0,
        long pointsPerYuan = DefaultPointsPerYuan)
    {
        if (lineCouponDiscounts.Count != lines.Count || lineActivityDiscounts.Count != lines.Count)
        {
            throw new ArgumentException("每行的券优惠与活动优惠数组长度必须与订单行数量一致。");
        }

        var results = new List<OrderLineAmount>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            // 1) 原行金额
            var original = Round2(line.UnitPrice * line.Quantity);

            // 2/3) 优惠额（上游已分摊好，这里只舍入）
            var activity = Round2(lineActivityDiscounts[i]);
            var coupon = Round2(lineCouponDiscounts[i]);

            if (activity < 0m) activity = 0m;
            if (coupon < 0m) coupon = 0m;

            // 4) 行应付 = 原行 − 活动 − 券，然后封底
            var payable = Round2(original - activity - coupon);
            if (payable < 0m) payable = 0m;

            var floored = false;
            if (payable == 0m)
            {
                // 券 / 活动把行压成 0 → 抬到 0.01。
                // 只有一种情况允许保持 0：原行金额本来就是 0（数量 0 的异常行）。
                if (original > 0m)
                {
                    payable = 0.01m;
                    floored = true;
                }
            }

            results.Add(new OrderLineAmount(
                line.SkuId, line.Quantity, original, activity, coupon, payable, floored));
        }

        // 5) 商品总额 = Σ 各行应付，**由行累加，不重算**
        var goodsTotal = Round2(results.Sum(a => a.PayableAmount));

        // 6) 运费：商品实付达到包邮门槛则免运费
        var freight = Round2(freightRule.Freight);
        if (freightRule.FreeShippingThreshold > 0m && goodsTotal >= freightRule.FreeShippingThreshold)
        {
            freight = 0m;
        }

        // 7) 积分抵扣 = 抵扣积分数 ÷ 汇率。
        //
        // 🔴 汇率**不是常量 100**：它是积分规则里的一项（PointRuleConfig 的
        // points_per_yuan，后台可改）。这里写死 100 的话，运营把汇率改成 200，
        // 订单侧照旧按 100 算 —— 结算页与实付对不上，而且不会有任何报错。
        // 汇率由积分服务给出，调用方从 IPointPort 取。
        var rate = pointsPerYuan > 0 ? pointsPerYuan : DefaultPointsPerYuan;
        var pointsDeduction = pointsToUse / (decimal)rate;
        if (pointsDeduction < 0m) pointsDeduction = 0m;

        // 8) 实付 = 商品总额 + 运费 − 积分抵扣，下限 0.00
        var payableAmount = Round2(goodsTotal + freight - pointsDeduction);
        if (payableAmount < 0m) payableAmount = 0m;

        return new OrderAmount(results, goodsTotal, freight, pointsDeduction, payableAmount);
    }

    /// <summary>按比例把整单券优惠额分摊到各行。</summary>
    /// <param name="lines">订单行。</param>
    /// <param name="couponDiscountTotal">整单券优惠额。</param>
    /// <returns>每行分摊到的券优惠额，长度与 lines 一致。</returns>
    /// <remarks>
    /// 按各行原行金额的比例分摊，<b>余数全给金额最大的那一行</b>。
    /// 直接按比例 Round 会丢掉几分钱，导致「各行优惠之和 ≠ 整单优惠」，
    /// 对账时又是一次「差一分钱」。
    /// </remarks>
    public static IReadOnlyList<decimal> AllocateCouponDiscount(
        IReadOnlyList<OrderLineInput> lines, decimal couponDiscountTotal)
    {
        var result = new decimal[lines.Count];
        if (lines.Count == 0 || couponDiscountTotal <= 0m) return result;

        var originals = lines.Select(a => Round2(a.UnitPrice * a.Quantity)).ToArray();
        var total = Round2(originals.Sum());
        if (total <= 0m) return result;

        var largestIndex = 0;
        for (var i = 0; i < originals.Length; i++)
        {
            if (originals[i] > originals[largestIndex]) largestIndex = i;
        }

        // 先按比例给每一行都分一份，再把舍入产生的余数补给金额最大的那一行。
        // 注意<b>不能</b>在算到最大行时就把总额全给它然后 break——那样排在它后面的行
        // 一分钱都分不到，整单优惠会全部压在第一行上（踩过：两行 51 / 49 分摊 100 元，
        // 结果第一行减了 100、第二行减了 0）。
        var allocated = 0m;
        for (var i = 0; i < originals.Length; i++)
        {
            result[i] = Round2(couponDiscountTotal * originals[i] / total);
            allocated = Round2(allocated + result[i]);
        }

        var remainder = Round2(couponDiscountTotal - allocated);
        if (remainder != 0m)
        {
            result[largestIndex] = Round2(result[largestIndex] + remainder);
        }

        return result;
    }
}
