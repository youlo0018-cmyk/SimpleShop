using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace MerchantPlatformService.Domain.Entities;

/// <summary>平台装修配置（首页 / 我的页）。</summary>
/// <remarks>
/// <b>草稿与已发布分成两列存</b>，而不是「一份数据 + 状态标记」：
/// 运营在草稿里随便改，线上那份一个字节都不能动。
/// 存成两份字符串时「保存草稿不影响线上」是<b>天然成立</b>的，
/// 不需要额外的版本切换逻辑，也不会出现「切错了、把草稿推上线」这类事故。
/// </remarks>
[Table(Name = "platform_app_config")]
public class PlatformAppConfig : AdminEntityBase
{
    /// <summary>已发布版本号，每次发布 +1。<b>0 表示从未发布过</b>。</summary>
    [Column(Name = "version")]
    public int Version { get; set; }

    /// <summary>草稿 JSON（BUSINESS.md 16.5 的结构）。</summary>
    [Column(Name = "draft_json")]
    public string DraftJson { get; set; } = string.Empty;

    /// <summary>已发布 JSON，小程序只读这一份。</summary>
    [Column(Name = "published_json")]
    public string PublishedJson { get; set; } = string.Empty;
}

/// <summary>商户装修配置（只有店铺页）。</summary>
/// <remarks>
/// 与平台装修结构相同但<b>独立成表</b>，而不是加个 scope 字段共用一张：
/// 两者的查询维度完全不同（一个按平台查、一个按商户查），
/// 混在一张表里每次查询都得带上 scope 条件，索引也会退化。
/// </remarks>
[Table(Name = "merchant_app_config")]
public class MerchantAppConfig : AdminEntityBase
{
    /// <summary>已发布版本号，每次发布 +1。<b>0 表示从未发布过</b>。</summary>
    [Column(Name = "version")]
    public int Version { get; set; }

    /// <summary>草稿 JSON（<c>pages</c> 只含 <c>store</c>）。</summary>
    [Column(Name = "draft_json")]
    public string DraftJson { get; set; } = string.Empty;

    /// <summary>已发布 JSON，小程序只读这一份。</summary>
    [Column(Name = "published_json")]
    public string PublishedJson { get; set; } = string.Empty;
}
