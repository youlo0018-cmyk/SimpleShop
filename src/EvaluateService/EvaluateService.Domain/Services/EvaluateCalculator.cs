namespace EvaluateService.Domain.Services;

/// <summary>评价相关的纯计算规则。</summary>
/// <remarks>
/// 放在 Domain 层而不是 Handler 里，是为了让「规格文案怎么拼」「均分怎么算」
/// 这些<strong>会被前端直接看到</strong>的规则能被单元测试覆盖，而不必起服务跑端到端。
/// </remarks>
public static class EvaluateCalculator
{
    /// <summary>单条评价最多几张图。</summary>
    public const int MaxImages = 9;

    /// <summary>规格展示最多列几个，超出折叠。</summary>
    public const int MaxDisplaySpecs = 3;

    /// <summary>首评可追评的最大条数。</summary>
    public const int MaxAppends = 3;

    /// <summary>追评有效天数（自首评起）。</summary>
    public const int AppendWindowDays = 30;

    /// <summary>无评价商品的默认展示分。</summary>
    public const decimal DefaultScore = 5.0m;

    /// <summary>拼规格展示文案：最多列 <see cref="MaxDisplaySpecs"/> 个，超出显示「等 N 个规格」。</summary>
    /// <param name="specs">规格文本列表，按购买顺序。</param>
    /// <returns>展示文案；没有有效规格返回空串。</returns>
    /// <remarks>
    /// 为什么要折叠：一条订单可能买了同一 SPU 的七八个规格，全列出来能把评价卡片撑爆。
    /// 去重后再算数量，否则「红色 / M」买了两次会被算成两个规格，
    /// 文案显示「等 1 个规格」——但实际只有一个，很荒唐。
    /// </remarks>
    public static string FormatSpecs(IReadOnlyCollection<string> specs)
    {
        if (specs.Count == 0) return string.Empty;

        // 去重但保持原顺序：顺序变了，用户看到的规格顺序会跟下单时对不上
        var distinct = new List<string>();
        foreach (var spec in specs)
        {
            var text = (spec ?? string.Empty).Trim();
            if (text.Length > 0 && !distinct.Contains(text, StringComparer.Ordinal))
            {
                distinct.Add(text);
            }
        }

        if (distinct.Count == 0) return string.Empty;

        if (distinct.Count <= MaxDisplaySpecs)
        {
            return string.Join("、", distinct);
        }

        var head = string.Join("、", distinct.Take(MaxDisplaySpecs));
        return $"{head} 等 {distinct.Count} 个规格";
    }

    /// <summary>校验「文字或图片至少有一项」。</summary>
    /// <param name="content">评价文字。</param>
    /// <param name="images">图片 URL 集合。</param>
    /// <param name="error">不通过时的原因，通过时为空串。</param>
    /// <returns>通过返回 true。</returns>
    /// <remarks>
    /// 只看「有没有」，不看内容质量。全空白 + 无图是一条没有任何信息的评价，
    /// 收下来只会拉低店铺评分的水准，还占版面。
    /// </remarks>
    public static bool ValidateContentAndImages(string? content, IReadOnlyCollection<string> images,
        out string error)
    {
        var hasText = !string.IsNullOrWhiteSpace(content);
        var hasImage = images is { Count: > 0 };

        if (hasText || hasImage)
        {
            error = string.Empty;
            return true;
        }

        error = "请至少填写评价内容或上传一张图片";
        return false;
    }

    /// <summary>计算 SPU 均分（首评星级均值，两位小数）。</summary>
    /// <param name="starScores">首评星级集合。</param>
    /// <returns>均分；无评价返回 0。</returns>
    /// <remarks>
    /// 不用默认的银行家舍入（ToEven）：3 星 + 4 星 = 3.5 这类 .5 值很常见，
    /// ToEven 会把 3.45 舍成 3.4、把 3.55 舍成 3.6，用户看到的均分会前后不一致。
    /// 用 AwayFromZero 让中文语境的「四舍五入」真正成立。
    /// </remarks>
    public static decimal AverageScore(IReadOnlyCollection<int> starScores)
    {
        if (starScores.Count == 0) return 0m;

        var sum = 0L;
        foreach (var star in starScores) sum += star;

        var average = (decimal)sum / starScores.Count;
        return decimal.Round(average, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>计算店铺评分：有评价商品的均分的平均值。</summary>
    /// <param name="spuScores">参与统计的商品均分集合。</param>
    /// <returns>店铺评分；无商品返回 0。</returns>
    /// <remarks>
    /// <b>入参必须已经排除了零评价商品。</b>规格 14.5 明确写了这一点：
    /// 无评价商品的默认展示分是 5.0，把它们算进来会让店铺评分虚高——
    /// 新店刷 10 个零评价商品，评分直接 5.0，比认真做生意的店还高。
    /// 所以过滤必须由调用方用「实际有评价」的数据完成，本方法不做隐式过滤，
    /// 免得调用方误传一批 5.0 进来还以为方法会替它剔除。
    /// </remarks>
    public static decimal MerchantRating(IReadOnlyCollection<decimal> spuScores)
    {
        if (spuScores.Count == 0) return 0m;

        decimal sum = 0m;
        foreach (var score in spuScores) sum += score;

        var average = sum / spuScores.Count;
        return decimal.Round(average, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>取展示用评分：无评价显示 5.0，有评价显示真实均分。</summary>
    /// <param name="averageScore">真实均分，0 表示还没有评价。</param>
    /// <returns>展示评分。</returns>
    /// <remarks>
    /// 为什么无评价给 5.0 而不是 0.0：0 分会被理解成「很差」，
    /// 而「还没人评价」是中性的。规格 14.4 也明确写了「无评价时评分显示 5.0」。
    /// </remarks>
    public static decimal DisplayScore(decimal averageScore)
        => averageScore > 0m ? averageScore : DefaultScore;

    /// <summary>追评是否还在有效期内。</summary>
    /// <param name="firstEvaluatedAt">首评时间 UTC。</param>
    /// <param name="now">当前时间 UTC。</param>
    /// <returns>在有效期内返回 true。</returns>
    /// <remarks>
    /// 首评之后 30 天内可追评。超期不是「禁止」而是「过期」：
    /// 商品用了半年才想起追加使用体验，那条追评对其他人没有参考价值，
    /// 反而会让评价列表被过期信息刷屏。
    /// </remarks>
    public static bool IsAppendWindowOpen(DateTime firstEvaluatedAt, DateTime now)
        => now <= firstEvaluatedAt.AddDays(AppendWindowDays);
}
