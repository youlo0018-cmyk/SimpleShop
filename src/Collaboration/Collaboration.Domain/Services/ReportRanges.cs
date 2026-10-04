namespace Collaboration.Domain.Services;

/// <summary>报表时间范围（BUSINESS.md 17 规定的四个档位）。</summary>
/// <remarks>
/// 用**枚举而不是自由起止时间**：后台就是这四个档，运营自己拼「9 月 1 号到 9 月 30 号」
/// 会算出一堆口径不一致的报表（时区、是否含当天、跨月怎么算）。档位固定下来，
/// 每个档位的口径就是唯一的一份实现，比较两天的数字才有意义。
///
/// <para><b>放在 Collaboration 而不是某个业务服务</b>：工作台报表在订单服务、
/// 营销与秒杀效果报表在营销服务、积分报表在积分服务，三边都要同一套区间口径。
/// 各自复制一份的话，改了其中一处，另外两个服务的「今日」就会和它对不上——
/// 而运营同时开着三个报表页，看到自相矛盾的数字只会怀疑系统坏了。</para>
/// </remarks>
public static class ReportRanges
{
    /// <summary>今日。</summary>
    public const int Today = 1;

    /// <summary>昨日。</summary>
    public const int Yesterday = 2;

    /// <summary>近 7 天（含今天）。</summary>
    public const int Last7Days = 3;

    /// <summary>近 30 天（含今天）。</summary>
    public const int Last30Days = 4;

    /// <summary>把档位换算成起止时间。</summary>
    /// <param name="range">档位。</param>
    /// <param name="nowUtc">当前时间。</param>
    /// <returns>起止时间，区间为<b>左闭右开</b>。</returns>
    /// <remarks>
    /// <b>左闭右开</b>（<c>[From, To)</c>）而不是左右都闭：左右都闭的话，
    /// 「近 7 天」和「近 30 天」会在同一天午夜各算两次，于是两次统计的口径对不上。
    ///
    /// <para><b>「今日」按 UTC 日界</b>，与全项目「时间一律存 UTC」一致。
    /// 混用本地日界的话，报表在每天早上八点前后会突然少一笔凌晨的订单。</para>
    /// </remarks>
    public static (DateTime From, DateTime To) Resolve(int range, DateTime nowUtc)
    {
        var todayStart = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc);

        return range switch
        {
            Today => (todayStart, todayStart.AddDays(1)),
            Yesterday => (todayStart.AddDays(-1), todayStart),
            Last7Days => (todayStart.AddDays(-6), todayStart.AddDays(1)),
            Last30Days => (todayStart.AddDays(-29), todayStart.AddDays(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(range), range, "报表时间范围只能是 1~4")
        };
    }

    /// <summary>档位的中文名（后台展示用，不返回数字枚举）。</summary>
    /// <param name="range">档位。</param>
    /// <returns>中文名。</returns>
    public static string NameOf(int range)
        => range switch
        {
            Today => "今日",
            Yesterday => "昨日",
            Last7Days => "近 7 天",
            Last30Days => "近 30 天",
            _ => "未知范围"
        };
}
