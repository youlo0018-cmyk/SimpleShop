using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace MerchantPlatformService.Domain.Entities;

/// <summary>平台级配置：目前只有地区地址数据。</summary>
/// <remarks>
/// <b>为什么地区数据存 JSON 而不是拆成省 / 市 / 区县三张表</b>：
/// 它是**只读的展示型数据**，运营整份导入、整体清空，从不按单个区县做增删改。
/// 拆三张表意味着导入 3000 多个区县要做 3000 次插入、还要维护父子关系的完整性，
/// 而读的时候整份读出来缓存即可。三张表唯一的好处是「能按区县反查」，
/// 而这个业务里没有这个需求。
/// </remarks>
[Table(Name = "platform_config")]
public class PlatformConfig : AdminEntityBase
{
    /// <summary>
    /// 三级地区数据的 JSON 数组，<b>空字符串表示回落到内置默认库</b>。
    /// </summary>
    /// <remarks>
    /// 存空串而不是 NULL：规格要求「恢复默认」= 提交空字符串、清空配置、
    /// 回落内置默认。用 NULL 会让「没配过」与「配了但清空了」两种状态混在一起，
    /// 以后想统计「有多少平台用了自定义地区」就做不到了。
    /// </remarks>
    [Column(Name = "regions_json")]
    public string RegionsJson { get; set; } = string.Empty;
}
