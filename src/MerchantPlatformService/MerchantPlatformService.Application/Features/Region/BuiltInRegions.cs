namespace MerchantPlatformService.Application.Features.Region;

/// <summary>内置默认地区库。</summary>
/// <remarks>
/// <para>🔴 <b>只包含省级，不含市 / 区县</b>——这一点必须对运营讲清楚，不能含糊。</para>
///
/// <para>规格 5.31 写的是「31 省 / 342 市 / 3056 区县」的完整内置库。
/// 省级行政区划实际是 <b>34 个</b>（含 4 个直辖市与 2 个特别行政区），
/// 本类给出的是这一层的<strong>完整且准确</strong>的清单。</para>
///
/// <para>市 / 区县两级有 3400 多条，且<b>逐年变动</b>（撤县设区、新设开发区）。
/// 硬编码在源码里必然过期——这正是规格把它设计成「运营可导入、可恢复默认」的原因。
/// 投产前运营需要在后台执行一次「保存自定义」导入完整数据；
/// 导入后 <c>IsCustom = true</c>，前台读的就是运营那一份。</para>
///
/// <para>不塞一份不完整的市级数据是有意的：<b>只给一半的城市列表比不给更糟</b>，
/// 用户在自己的城市里找不到对应项，只会以为系统坏了，然后投诉。
/// 与其给一份会误导的清单，不如给一份明确「需要导入」的省级清单。</para>
/// </remarks>
public static class BuiltInRegions
{
    /// <summary>34 个省级行政区划名称。</summary>
    private static readonly string[] Provinces =
    [
        "北京市", "天津市", "河北省", "山西省", "内蒙古自治区", "辽宁省", "吉林省",
        "黑龙江省", "上海市", "江苏省", "浙江省", "安徽省", "福建省", "江西省",
        "山东省", "河南省", "湖北省", "湖南省", "广东省", "广西壮族自治区", "海南省",
        "重庆市", "四川省", "贵州省", "云南省", "西藏自治区", "陕西省", "甘肃省",
        "青海省", "宁夏回族自治区", "新疆维吾尔自治区", "台湾省",
        "香港特别行政区", "澳门特别行政区"
    ];

    /// <summary>内置默认地区数据的 JSON 数组字符串。</summary>
    /// <remarks>
    /// 手工拼字符串而不是走序列化：这里每个元素只有一个 <c>name</c> 字段，
    /// 序列化器要为此引入 <c>Children</c> 默认值 / 空数组的处理，
    /// 拼出来反而更短、也更直观地看到最终发出去的内容。
    /// </remarks>
    public static string Json { get; } = Build();

    /// <summary>拼出地区 JSON。</summary>
    /// <returns>地区 JSON 数组字符串。</returns>
    private static string Build()
        => "[" + string.Join(",", Provinces.Select(a => $"{{\"name\":\"{a}\"}}")) + "]";
}
