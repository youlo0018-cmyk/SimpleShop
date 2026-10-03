namespace EvaluateService.Application.Features.Evaluate;

/// <summary>一条评价（含追评与回复）。</summary>
/// <param name="EvaluateId">评价 Id。</param>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SpuName">商品名快照。</param>
/// <param name="SkuSpecs">规格展示文案（已折叠为「等 N 个规格」）。</param>
/// <param name="SkuIds">本条评价覆盖的 SKU Id 集合（详情页按此过滤）。</param>
/// <param name="StarScore">星级。</param>
/// <param name="Content">评价内容。</param>
/// <param name="Images">图片 URL 列表。</param>
/// <param name="IsAnonymous">是否匿名。</param>
/// <param name="CustomerName">昵称；匿名时为「匿名用户」。</param>
/// <param name="CreatedAt">发表时间（展示用字符串）。</param>
/// <param name="IsHidden">是否被隐藏。</param>
/// <param name="HiddenReason">隐藏原因（仅后台可见）。</param>
/// <param name="Appends">追评列表。</param>
/// <param name="Replies">回复列表。</param>
public sealed record EvaluateDto(
    long EvaluateId, long SpuId, string SpuName, string SkuSpecs, IReadOnlyList<long> SkuIds,
    int StarScore, string Content, IReadOnlyList<string> Images, bool IsAnonymous,
    string CustomerName, string CreatedAt, bool IsHidden, string HiddenReason,
    IReadOnlyList<AppendDto> Appends, IReadOnlyList<ReplyDto> Replies);

/// <summary>追评。</summary>
/// <param name="AppendId">追评 Id。</param>
/// <param name="StarScore">星级，0 表示未打分。</param>
/// <param name="Content">内容。</param>
/// <param name="Images">图片 URL 列表。</param>
/// <param name="CustomerName">昵称。追评跟随首评的匿名设置，没有独立的匿名字段。</param>
/// <param name="CreatedAt">发表时间。</param>
public sealed record AppendDto(
    long AppendId, int StarScore, string Content, IReadOnlyList<string> Images,
    string CustomerName, string CreatedAt);

/// <summary>回复。</summary>
/// <param name="ReplyId">回复 Id。</param>
/// <param name="AppendId">被回复的追评 Id，0 表示回复首评。</param>
/// <param name="Content">回复内容。</param>
/// <param name="ReplyType">1 商户 / 2 平台。</param>
/// <param name="ReplyTypeName">回复主体中文名。</param>
/// <param name="ReplyByName">回复人姓名。</param>
/// <param name="CreatedAt">回复时间。</param>
public sealed record ReplyDto(
    long ReplyId, long AppendId, string Content, int ReplyType, string ReplyTypeName,
    string ReplyByName, string CreatedAt);

/// <summary>评价分页结果。</summary>
/// <param name="Items">当前页评价。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
/// <param name="AverageScore">该 SPU 的均分，两位小数。无评价为 0。</param>
/// <param name="DisplayScore">展示用评分，无评价显示 5.0。</param>
/// <param name="Count">评价条数（只数首评）。</param>
public sealed record EvaluatePageResult(
    IReadOnlyList<EvaluateDto> Items, long Total, int Page, int PageSize,
    decimal AverageScore, decimal DisplayScore, int Count);

/// <summary>可评价商品（结算后「去评价」入口）。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SpuName">商品名快照。</param>
/// <param name="SkuSpecs">本单买过的规格文案。</param>
/// <param name="MainImage">商品主图（由商品服务补齐，可为空）。</param>
/// <param name="Evaluated">是否已评价。</param>
public sealed record EvaluableItemDto(
    string OrderNo, long SpuId, string SpuName, string SkuSpecs, string MainImage, bool Evaluated);

/// <summary>发表首评的结果。</summary>
/// <param name="EvaluateId">评价 Id。</param>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SkuIds">服务端自动推导的 SKU 标记集合。</param>
public sealed record PublishEvaluateResult(long EvaluateId, long SpuId, IReadOnlyList<long> SkuIds);
