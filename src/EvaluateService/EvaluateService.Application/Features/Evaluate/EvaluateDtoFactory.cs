using EvaluateService.Domain.Entities;
// `Features/Evaluate` 这个命名空间段会遮蔽同名实体 `Evaluate`（CODING_STANDARD §6 第 1 条）。
// 不加别名的话参数写 `Evaluate evaluate` 报 CS0118「Evaluate 是命名空间，但此处被当做类型」。
using EvaluateEntity = EvaluateService.Domain.Entities.Evaluate;

namespace EvaluateService.Application.Features.Evaluate;

/// <summary>把领域实体翻译成对外 DTO 的公用装配器。</summary>
/// <remarks>
/// 单独抽出来是因为「C 端列表 / 我的评价 / 后台列表 / 详情」四条路径要拼的字段完全一样，
/// 各写一份的话改一处漏三处——最典型的是匿名展示：漏了就会把客户的真实昵称泄露给所有人。
/// </remarks>
internal static class EvaluateDtoFactory
{
    /// <summary>匿名评价在 C 端的固定展示名。</summary>
    public const string AnonymousName = "匿名用户";

    /// <summary>展示时间格式：本地时区，不带时区后缀。</summary>
    private const string TimeFormat = "yyyy-MM-dd HH:mm";

    /// <summary>把实体 UTC 时间格式化成字符串（**仍是 UTC**）。</summary>
    /// <param name="utc">UTC 时间。</param>
    /// <returns>UTC 展示字符串。</returns>
    /// <remarks>
    /// DATA_SPEC 4.8：后端返回 UTC 字符串，前端统一格式化为 Asia/Shanghai。
    /// 服务端 ToLocalTime 会让镜像时区决定显示时间（小程序侧再转一次就差 8 小时）。
    /// </remarks>
    public static string FormatTime(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString(TimeFormat);

    /// <summary>拆分逗号分隔的图片列表。</summary>
    /// <param name="images">逗号分隔的图片 URL。</param>
    /// <returns>图片列表；空串返回空列表。</returns>
    public static IReadOnlyList<string> SplitImages(string images)
    {
        if (string.IsNullOrWhiteSpace(images)) return [];

        return images.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>拼接图片列表。</summary>
    /// <param name="images">图片 URL 列表。</param>
    /// <returns>逗号分隔的字符串。</returns>
    public static string JoinImages(IReadOnlyList<string>? images)
        => images is null or { Count: 0 } ? string.Empty : string.Join(',', images);

    /// <summary>取展示昵称：匿名一律显示「匿名用户」。</summary>
    /// <param name="isAnonymous">是否匿名。</param>
    /// <param name="customerName">真实昵称。</param>
    /// <param name="forAdmin">是否给后台看（后台可见真实昵称）。</param>
    /// <returns>展示昵称。</returns>
    public static string DisplayName(bool isAnonymous, string customerName, bool forAdmin)
        => isAnonymous && !forAdmin ? AnonymousName : customerName;

    /// <summary>装配一条评价（含追评与回复）。</summary>
    /// <param name="evaluate">首评实体。</param>
    /// <param name="skuIds">SKU 标记集合。</param>
    /// <param name="appends">追评列表。</param>
    /// <param name="replies">回复列表。</param>
    /// <param name="forAdmin">是否给后台看（后台可见真实昵称与隐藏原因）。</param>
    /// <returns>评价 DTO。</returns>
    public static EvaluateDto Build(
        EvaluateEntity evaluate,
        IReadOnlyList<long> skuIds,
        IReadOnlyList<EvaluateAppend> appends,
        IReadOnlyList<EvaluateReply> replies,
        bool forAdmin)
    {
        var appendDtos = appends.Select(a => new AppendDto(
            a.Id, a.StarScore, a.Content, SplitImages(a.Images),
            DisplayName(false, a.CustomerName, forAdmin), FormatTime(a.CreatedAt))).ToList();

        var replyDtos = replies.Select(r => new ReplyDto(
            r.Id, r.AppendId, r.Content, r.ReplyType,
            EvaluateReplyTypes.NameOf(r.ReplyType), r.ReplyByName, FormatTime(r.CreatedAt))).ToList();

        return new EvaluateDto(
            evaluate.Id, evaluate.SpuId, evaluate.SpuName, evaluate.SkuSpecs, skuIds,
            evaluate.StarScore, evaluate.Content, SplitImages(evaluate.Images), evaluate.IsAnonymous,
            DisplayName(evaluate.IsAnonymous, evaluate.CustomerName, forAdmin),
            FormatTime(evaluate.CreatedAt),
            evaluate.IsHidden, forAdmin ? evaluate.HiddenReason : string.Empty,
            appendDtos, replyDtos);
    }
}
