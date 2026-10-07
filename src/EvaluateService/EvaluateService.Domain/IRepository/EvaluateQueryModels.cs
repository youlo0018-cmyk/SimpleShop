using EvaluateService.Domain.Entities;

namespace EvaluateService.Domain.IRepository;

/// <summary>首评连同它的 SKU 标记。</summary>
/// <param name="Evaluate">首评实体。</param>
/// <param name="SkuRefs">该评价覆盖的全部 SKU。</param>
public sealed record EvaluateWithRefs(Evaluate Evaluate, IReadOnlyList<EvaluateSkuRef> SkuRefs);

/// <summary>SPU 聚合分（只由首评计算）。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="AverageScore">首评星级均值，保留两位小数。</param>
/// <param name="Count">首评条数。</param>
public sealed record SpuRating(long SpuId, decimal AverageScore, int Count);

/// <summary>评价分页结果。</summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record PagedEvaluates(IReadOnlyList<Evaluate> Items, long Total, int Page, int PageSize);

/// <summary>后台评价列表的过滤条件。</summary>
/// <param name="SpuId">SPU Id，0 表示不限。</param>
/// <param name="MerchantId">商户 Id，0 表示不限。</param>
/// <param name="StarScore">星级，0 表示不限。</param>
/// <param name="OnlyHidden">true 只看已隐藏，false 不看隐藏。</param>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record EvaluateAdminFilter(
    long SpuId = 0,
    long MerchantId = 0,
    int StarScore = 0,
    bool OnlyHidden = false,
    /// <summary>后台搜索框关键词：商品名 / 评价内容模糊匹配，空表示不限。</summary>
    string Keyword = "",
    int Page = 1,
    int PageSize = 20);
